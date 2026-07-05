using System;

using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace AcrylicXIV.Graphics;

/// <summary>
/// Drives the backdrop blur at a deterministic, thread-safe point in the frame.
///
/// FFXIV records render commands on the main thread and replays them on a dedicated render thread, which owns
/// the D3D11 immediate context. Touching that context from the main thread races the render thread and crashes
/// the driver, so all D3D must happen on the render thread. But the render thread only exposes low-level command
/// dispatches, and the per-frame pattern of swap-chain back-buffer binds is unstable (DLSS / frame-generation
/// change it frame to frame), so we cannot pick "the right bind" by counting.
///
/// Instead we use ffxiv-vr's injection trick:
///   1. Hook the main-thread "UI pushback" (the moment just before the HUD is recorded) and, from there,
///      <see cref="RenderPipelineInjector.QueueBlurMarker"/> a sentinel command into the render queue.
///   2. Hook the render-thread "set render target" dispatch. When it replays our sentinel (recognised by the
///      marker <c>numRenderTargets</c> value), we blur the swap-chain back buffer right there — on the render
///      thread, exactly once per frame, right before the HUD draws — regardless of how the scene was composited.
/// </summary>
internal sealed unsafe class RenderHooks : IDisposable
{
    private delegate void SetRenderTargetDelegate(Device* device, SetRenderTargetCommand* command);

    // The render-thread "set render target" dispatch. The signature starts with E8 (a call), so it MUST be
    // resolved via [Signature]/InitializeFromAttributes (which follows the call target). Verified vs ffxiv-vr.
    [Signature("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? F3 0F 10 5F 18",
        DetourName = nameof(SetRenderTargetDetour),
        Fallibility = Fallibility.Fallible)]
    private Hook<SetRenderTargetDelegate>? setRenderTargetHook = null;

    // The main-thread "UI pushback" call, invoked once per frame right before the HUD is recorded. We inject our
    // blur marker here so it replays (on the render thread) just before the UI is drawn. Verified vs ffxiv-vr.
    private delegate void PushbackUiDelegate(ulong a, ulong b);
    [Signature("E8 ?? ?? ?? ?? EB ?? E8 ?? ?? ?? ?? 4C 8D 5C 24 50",
        DetourName = nameof(PushbackUiDetour),
        Fallibility = Fallibility.Fallible)]
    private Hook<PushbackUiDelegate>? pushbackUiHook = null;

    // UIModule::Draw2D — the native HUD draw entry, once per frame on the main thread. We inject a POST-UI marker
    // AFTER its Original so it replays right after the UI is recorded, giving us a render-thread callback with the
    // UI already composited (used to read the per-pixel UI coverage). Sig from FFXIVClientStructs main.
    private delegate void Draw2DDelegate(void* uiModule);
    [Signature("48 83 EC ?? ?? ?? ?? FF 50 ?? 48 8B C8 48 83 C4 ?? E9 ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? ?? 40 53 48 83 EC ?? ?? ?? ?? 48 8B D9",
        DetourName = nameof(Draw2DDetour),
        Fallibility = Fallibility.Fallible)]
    private Hook<Draw2DDelegate>? draw2DHook = null;

    private readonly BackdropBlurRenderer renderer;
    private readonly Func<Configuration> getConfig;
    private readonly RenderPipelineInjector injector;

    // Guards the D3D work against Dispose: the detour holds this lock while blurring, and Dispose acquires it
    // (after disabling the hook) to drain any in-flight detour before the renderer's D3D resources are freed.
    private readonly object renderLock = new();
    private bool disposed;

    // Set on the first managed exception in our own dispatch logic; permanently stops us from disturbing the
    // render thread again.
    private bool detourFailed;
    private long errorThrottle;

    /// <summary>True if the render-thread signature resolved and the hook is live.</summary>
    public bool Available => setRenderTargetHook != null;

    /// <summary>True if the full deterministic pipeline (render-thread hook + pushback hook + injector) is live.</summary>
    public bool DeterministicReady => setRenderTargetHook != null && pushbackUiHook != null && injector.Available;

    public RenderHooks(BackdropBlurRenderer renderer, Func<Configuration> getConfig,
        RenderPipelineInjector injector, IGameInteropProvider gameInterop)
    {
        this.renderer = renderer;
        this.getConfig = getConfig;
        this.injector = injector;

        gameInterop.InitializeFromAttributes(this);
        // Only enable the marker PRODUCERS (pushback/draw2D) if the render-thread CONSUMER resolved. Otherwise our
        // sentinel commands would enter the game's render queue with nothing to strip them out, and the real
        // dispatch would try to interpret them as genuine set-render-target commands (crash/corruption).
        if (setRenderTargetHook != null)
        {
            setRenderTargetHook.Enable();
            pushbackUiHook?.Enable();
            draw2DHook?.Enable();
        }
    }

    // --- Main thread: inject the blur marker just before the HUD is recorded. ---
    private void PushbackUiDetour(ulong a, ulong b)
    {
        if (!detourFailed && !disposed)
        {
            try
            {
                var config = getConfig();
                if (config.Enabled)
                    injector.QueueMarker(RenderPipelineInjector.BlurMarker);
            }
            catch (Exception ex)
            {
                detourFailed = true;
                Plugin.Log.Error(ex, "Queuing blur marker failed; disabling blur injection.");
            }
        }

        pushbackUiHook!.Original(a, b);
    }

    // --- Main thread: inject a POST-UI marker right after the HUD is recorded. ---
    private void Draw2DDetour(void* uiModule)
    {
        draw2DHook!.Original(uiModule);

        if (!detourFailed && !disposed)
        {
            try
            {
                var config = getConfig();
                // The under-UI compose always needs the post-UI callback; only DebugClear (a pre-UI-only flood)
                // has nothing to do here, but queuing the marker is harmless (the handler no-ops for it).
                if (config.Enabled)
                    injector.QueueMarker(RenderPipelineInjector.PostUiMarker);
            }
            catch (Exception ex)
            {
                detourFailed = true;
                Plugin.Log.Error(ex, "Queuing post-UI marker failed; disabling blur injection.");
            }
        }
    }

    // --- Render thread: replay the queue; act when we reach one of our injected markers. ---
    private void SetRenderTargetDetour(Device* device, SetRenderTargetCommand* command)
    {
        // Our markers are fake commands (null render target). Consume them WITHOUT forwarding to the game and do
        // our work in their place. Everything else is a real bind and must be forwarded unchanged.
        if (command != null)
        {
            var n = command->numRenderTargets;
            if (n == RenderPipelineInjector.BlurMarker)
            {
                HandlePreUiMarker();
                return;
            }
            if (n == RenderPipelineInjector.PostUiMarker)
            {
                HandlePostUiMarker();
                return;
            }
        }

        setRenderTargetHook!.Original(device, command);
    }

    // Pre-UI: the scene is composited, the HUD is about to draw. The under-UI masked blur is the default; the
    // debug toggles override it for diagnostics.
    //  - FullscreenTest: apply to the whole screen (compose is skipped post-UI) — blur everything, or, with
    //    DebugClear on, flood a flat colour so the HUD drawn on top confirms the injection point;
    //  - DebugShowUiMask: just zero alpha to observe the raw UI coverage post-UI;
    //  - otherwise: stash sharp+blurred scenes, blur, and zero alpha so the UI accumulates coverage.
    private void HandlePreUiMarker()
    {
        if (detourFailed)
            return;

        var config = getConfig();
        if (!config.Enabled)
            return;

        lock (renderLock)
        {
            if (disposed)
                return;

            try
            {
                if (config.FullscreenTest)
                {
                    if (config.DebugClear)
                    {
                        renderer.InvalidateUnderUiCapture();
                        renderer.DebugClearBound(); // flat colour over the whole screen; HUD on top confirms injection
                    }
                    else
                    {
                        renderer.BeginUnderUi(config); // blur the whole screen (compose skipped => no re-sharpen)
                    }
                }
                else if (config.DebugShowUiMask)
                {
                    renderer.InvalidateUnderUiCapture();
                    renderer.ClearBoundAlpha();
                }
                else
                {
                    renderer.BeginUnderUi(config);
                }
            }
            catch (Exception ex)
            {
                if (errorThrottle++ % 600 == 0)
                    Plugin.Log.Error(ex, "Backdrop blur draw failed (throttled).");
            }
        }
    }

    // Post-UI: the HUD has been drawn.
    //  - FullscreenTest + DebugClear (flat fill): nothing to do — the fill + HUD is the whole picture;
    //  - FullscreenTest (blur): compose in whole-screen mode so blur + material cover the entire screen;
    //  - DebugShowUiMask: replace the frame with the UI coverage (alpha) as greyscale;
    //  - otherwise: composite so the blur shows only under the UI.
    private void HandlePostUiMarker()
    {
        if (detourFailed)
            return;

        var config = getConfig();
        if (!config.Enabled)
            return;
        // Full-screen FILL has nothing to compose; every other mode composes (the compose shader switches into
        // whole-screen mode when FullscreenTest is set).
        if (config.FullscreenTest && config.DebugClear)
            return;

        lock (renderLock)
        {
            if (disposed)
                return;

            try
            {
                if (!config.FullscreenTest && config.DebugShowUiMask)
                    renderer.VisualizeBoundAlpha();
                else
                    renderer.ComposeUnderUi(config);
            }
            catch (Exception ex)
            {
                if (errorThrottle++ % 600 == 0)
                    Plugin.Log.Error(ex, "Backdrop compose failed (throttled).");
            }
        }
    }

    public void Dispose()
    {
        // Ordered teardown to avoid crashing the game's render thread:
        // 1. Stop PRODUCING markers (main-thread hooks) and stop doing GPU work — but keep the render-thread
        //    SetRenderTarget hook ENABLED so it still STRIPS any sentinel markers already sitting in the render
        //    command queue. If we disabled that stripper immediately, a leftover sentinel would be replayed into
        //    the real SetRenderTarget (numRenderTargets = a magic value) and the game would iterate thousands of
        //    bogus render targets and crash. This is reliably reproduced by unloading with the config window open,
        //    when the render thread is actively replaying the command buffer.
        draw2DHook?.Disable();
        pushbackUiHook?.Disable();
        lock (renderLock)
        {
            disposed = true; // no more blur, and drains any in-flight blur that holds this lock
        }

        // 2. Give the render thread a few frames to replay (and strip) any markers already queued this frame.
        if (setRenderTargetHook != null)
            System.Threading.Thread.Sleep(150);

        // 3. The queue is drained now; stop consuming and dispose the hooks.
        setRenderTargetHook?.Disable();
        draw2DHook?.Dispose();
        pushbackUiHook?.Dispose();
        setRenderTargetHook?.Dispose();
    }
}

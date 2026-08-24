using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Dalamud.Hooking;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AcrylicXIV.Graphics;

/// <summary>
/// Queues pre/post-UI markers on the main thread and handles them on the render thread, where D3D work is safe.
/// </summary>
internal sealed unsafe class RenderHooks : IDisposable
{
    private Hook<ImmediateContext.Delegates.DoSetTargetCommand>? setRenderTargetHook;
    private Hook<AtkServer.Delegates.ProcessUICommandsAlt>? pushbackUiHook;
    private Hook<UIModule.Delegates.Draw2D>? draw2DHook;

    private readonly BackdropBlurRenderer renderer;
    private readonly Func<Configuration> getConfig;
    private readonly RenderPipelineInjector injector;
    private readonly IGameGui gameGui;

    // Synchronizes render callbacks with D3D resource disposal.
    private readonly object renderLock = new();
    private bool disposed;

    private bool detourFailed;
    private long captureErrorThrottle;
    private long renderErrorThrottle;
    private bool uiFadeWasActive;
    private long uiFadeRecoveryStarted;

    public bool Available => setRenderTargetHook != null;

    public bool DeterministicReady =>
        setRenderTargetHook != null && pushbackUiHook != null && draw2DHook != null && injector.Available;

    public RenderHooks(BackdropBlurRenderer renderer, Func<Configuration> getConfig,
        RenderPipelineInjector injector, IGameInteropProvider gameInterop, IGameGui gameGui)
    {
        this.renderer = renderer;
        this.getConfig = getConfig;
        this.injector = injector;
        this.gameGui = gameGui;

        setRenderTargetHook = TryCreateHook<ImmediateContext.Delegates.DoSetTargetCommand>(gameInterop,
            ImmediateContext.Addresses.DoSetTargetCommand.Value, SetRenderTargetDetour, "render-thread");
        pushbackUiHook = TryCreateHook<AtkServer.Delegates.ProcessUICommandsAlt>(gameInterop,
            AtkServer.Addresses.ProcessUICommandsAlt.Value, PushbackUiDetour, "pre-UI");
        draw2DHook = TryCreateHook<UIModule.Delegates.Draw2D>(gameInterop,
            UIModule.Addresses.Draw2D.Value, Draw2DDetour, "post-UI");

        // Never queue markers without a consumer that can strip them from the game's command stream.
        if (setRenderTargetHook != null)
        {
            setRenderTargetHook.Enable();
            pushbackUiHook?.Enable();
            draw2DHook?.Enable();
        }
    }

    private static Hook<T>? TryCreateHook<T>(IGameInteropProvider gameInterop, nint address, T detour, string name)
        where T : Delegate
    {
        try
        {
            return gameInterop.HookFromAddress(address, detour);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Creating the {HookName} hook failed.", name);
            return null;
        }
    }

    private void PushbackUiDetour(AtkServer* atkServer, bool value)
    {
        if (!detourFailed && !disposed)
        {
            try
            {
                var config = getConfig();
                if (config.Enabled)
                {
                    var unitManager = RaptureAtkUnitManager.Instance();
                    var isUiFading = unitManager != null && unitManager->IsUiFading;
                    var skipFade = config.SkipUiFading && isUiFading;
                    var effectOpacity = UpdateUiFadeRecovery(config, isUiFading);
                    var frameState = !skipFade && config.ExcludeTargetNameplate && !config.FullscreenTest
                        ? CaptureNameplates(effectOpacity)
                        : PreUiFrameState.Create(skipFade, effectOpacity);
                    injector.QueuePreUiMarker(frameState);
                }
            }
            catch (Exception ex)
            {
                detourFailed = true;
                Plugin.Log.Error(ex, "Queuing blur marker failed; disabling blur injection.");
            }
        }

        pushbackUiHook!.Original(atkServer, value);
    }

    private void Draw2DDetour(UIModule* uiModule)
    {
        draw2DHook!.Original(uiModule);

        if (!detourFailed && !disposed)
        {
            try
            {
                var config = getConfig();
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

    private void SetRenderTargetDetour(ImmediateContext* immediateContext, RenderCommandSetTarget* command)
    {
        // Sentinel commands must never reach the game's handler.
        if (command != null)
        {
            var n = command->RenderTargetCount;
            if (n == RenderPipelineInjector.BlurMarker)
            {
                var frameState = injector.TakePreUiFrameState();
                try
                {
                    Span<System.Numerics.Vector4> rectangles =
                        stackalloc System.Numerics.Vector4[PreUiFrameState.MaxExcludedRegions];
                    var rectangleCount = frameState.CopyRectangles(rectangles);
                    HandlePreUiMarker(
                        frameState.SkipEffect, rectangles[..rectangleCount], frameState.EffectOpacity);
                }
                finally
                {
                    injector.MarkerConsumed();
                }
                return;
            }
            if (n == RenderPipelineInjector.PostUiMarker)
            {
                try
                {
                    HandlePostUiMarker();
                }
                finally
                {
                    injector.MarkerConsumed();
                }
                return;
            }
        }

        setRenderTargetHook!.Original(immediateContext, command);
    }

    private float UpdateUiFadeRecovery(Configuration config, bool isUiFading)
    {
        if (!config.SkipUiFading)
        {
            uiFadeWasActive = false;
            uiFadeRecoveryStarted = 0;
            return 1f;
        }

        if (isUiFading)
        {
            uiFadeWasActive = true;
            uiFadeRecoveryStarted = 0;
            return 0f;
        }

        if (uiFadeWasActive)
        {
            uiFadeWasActive = false;
            uiFadeRecoveryStarted = Stopwatch.GetTimestamp();
        }

        var duration = Math.Clamp(config.UiRecoveryFadeDuration, 0f, 2f);
        if (uiFadeRecoveryStarted == 0 || duration <= 0f)
            return 1f;

        var elapsed = Stopwatch.GetElapsedTime(uiFadeRecoveryStarted).TotalSeconds;
        if (elapsed >= duration)
        {
            uiFadeRecoveryStarted = 0;
            return 1f;
        }

        var t = Math.Clamp((float)(elapsed / duration), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private PreUiFrameState CaptureNameplates(float effectOpacity)
    {
        try
        {
            return PreUiFrameState.Capture(gameGui, effectOpacity);
        }
        catch (Exception ex)
        {
            if (captureErrorThrottle++ % 600 == 0)
                Plugin.Log.Error(ex, "Capturing nameplate bounds failed (throttled); rendering without exclusions.");
            return PreUiFrameState.Create(skipEffect: false, effectOpacity: effectOpacity);
        }
    }

    private void HandlePreUiMarker(bool skipEffect, ReadOnlySpan<System.Numerics.Vector4> nameplateRectangles,
        float effectOpacity)
    {
        if (skipEffect)
        {
            lock (renderLock)
            {
                if (!disposed)
                    renderer.InvalidateUnderUiCapture();
            }
            return;
        }

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
                        renderer.DebugClearBound();
                    }
                    else
                    {
                        renderer.BeginUnderUi(config, effectOpacity: effectOpacity);
                    }
                }
                else if (config.DebugShowUiMask)
                {
                    renderer.InvalidateUnderUiCapture();
                    renderer.ClearBoundAlpha(nameplateRectangles);
                }
                else
                {
                    renderer.BeginUnderUi(config, nameplateRectangles, effectOpacity);
                }
            }
            catch (Exception ex)
            {
                if (renderErrorThrottle++ % 600 == 0)
                    Plugin.Log.Error(ex, "Backdrop blur draw failed (throttled).");
            }
        }
    }

    private void HandlePostUiMarker()
    {
        if (detourFailed)
            return;

        var config = getConfig();
        if (!config.Enabled)
            return;
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
                if (renderErrorThrottle++ % 600 == 0)
                    Plugin.Log.Error(ex, "Backdrop compose failed (throttled).");
            }
        }
    }

    /// <summary>
    /// Stops marker production without disabling the consumer; call <see cref="Dispose"/> after queued markers drain.
    /// </summary>
    public Task BeginDispose()
    {
        if (disposed)
            return injector.BeginDrain();

        // Stop producers first, but keep the consumer enabled until queued markers drain. Otherwise a sentinel
        // RenderTargetCount can reach the game handler and crash the render thread.
        draw2DHook?.Disable();
        pushbackUiHook?.Disable();
        lock (renderLock)
        {
            disposed = true;
        }
        return injector.BeginDrain();
    }

    public void Dispose()
    {
        var drainTask = BeginDispose();
        if (!drainTask.IsCompletedSuccessfully)
            throw new InvalidOperationException("Queued render markers must drain before disposing the consumer hook.");

        setRenderTargetHook?.Disable();
        draw2DHook?.Dispose();
        pushbackUiHook?.Dispose();
        setRenderTargetHook?.Dispose();
    }
}

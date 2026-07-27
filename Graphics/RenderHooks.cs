using System;

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

    // Synchronizes render callbacks with D3D resource disposal.
    private readonly object renderLock = new();
    private bool disposed;

    private bool detourFailed;
    private long errorThrottle;

    public bool Available => setRenderTargetHook != null;

    public bool DeterministicReady =>
        setRenderTargetHook != null && pushbackUiHook != null && draw2DHook != null && injector.Available;

    public RenderHooks(BackdropBlurRenderer renderer, Func<Configuration> getConfig,
        RenderPipelineInjector injector, IGameInteropProvider gameInterop)
    {
        this.renderer = renderer;
        this.getConfig = getConfig;
        this.injector = injector;

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
                    injector.QueueMarker(RenderPipelineInjector.BlurMarker);
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
                HandlePreUiMarker();
                return;
            }
            if (n == RenderPipelineInjector.PostUiMarker)
            {
                HandlePostUiMarker();
                return;
            }
        }

        setRenderTargetHook!.Original(immediateContext, command);
    }

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
                        renderer.DebugClearBound();
                    }
                    else
                    {
                        renderer.BeginUnderUi(config);
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
                if (errorThrottle++ % 600 == 0)
                    Plugin.Log.Error(ex, "Backdrop compose failed (throttled).");
            }
        }
    }

    /// <summary>
    /// Stops marker production without disabling the consumer; call <see cref="Dispose"/> after queued markers drain.
    /// </summary>
    public void BeginDispose()
    {
        if (disposed)
            return;

        // Stop producers first, but keep the consumer enabled until queued markers drain. Otherwise a sentinel
        // RenderTargetCount can reach the game handler and crash the render thread.
        draw2DHook?.Disable();
        pushbackUiHook?.Disable();
        lock (renderLock)
        {
            disposed = true;
        }
    }

    public void Dispose()
    {
        BeginDispose();
        setRenderTargetHook?.Disable();
        draw2DHook?.Dispose();
        pushbackUiHook?.Dispose();
        setRenderTargetHook?.Dispose();
    }
}

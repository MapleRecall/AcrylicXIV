using System;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.Interop;

namespace AcrylicXIV.Graphics;

/// <summary>
/// Queues sentinel render commands for <see cref="RenderHooks"/> to consume on the render thread.
/// </summary>
internal sealed unsafe class RenderPipelineInjector : IDisposable
{
    internal const int BlurMarker = 0x1B70;
    internal const int PostUiMarker = 0x1B71;

    private bool disposed;

    public bool Available =>
        !disposed &&
        ThreadLocals.Addresses.NtCurrentTeb.Value != 0 &&
        Context.Addresses.AllocateCommand.Value != 0 &&
        Context.Addresses.PushBackCommand.Value != 0;

    /// <summary>
    /// Must run on the command-recording thread so the marker uses that thread's graphics context.
    /// </summary>
    public void QueueMarker(int sentinel)
    {
        if (!Available || disposed)
            return;

        var threadLocals = ThreadLocals.ThreadLocalInstance();
        if (threadLocals == null || !threadLocals->IsInitialized)
            return;

        var context = threadLocals->GraphicsKernelContext;
        if (context == null)
            return;

        var command = (RenderCommandSetTarget*)context->AllocateCommand((ulong)sizeof(RenderCommandSetTarget));
        if (command == null)
            return;

        *command = default;
        command->Type = RenderCommandType.SetTarget;
        command->RenderTargetCount = sentinel;

        context->PushBackCommand(command);
    }

    public void Dispose()
    {
        disposed = true;
    }
}

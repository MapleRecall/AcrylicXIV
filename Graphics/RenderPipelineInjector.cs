using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

    private readonly object markerLock = new();
    private readonly Queue<PreUiFrameState> preUiFrameStates = new(8);
    private TaskCompletionSource<bool>? drainCompletion;
    private int pendingMarkers;
    private bool acceptingMarkers = true;
    private bool disposed;

    public bool Available =>
        !disposed &&
        ThreadLocals.Addresses.NtCurrentTeb.Value != 0 &&
        Context.Addresses.AllocateCommand.Value != 0 &&
        Context.Addresses.PushBackCommand.Value != 0;

    /// <summary>
    /// Must run on the command-recording thread so the marker uses that thread's graphics context.
    /// </summary>
    public void QueueMarker(int sentinel) => QueueMarkerCore(sentinel, null);

    /// <summary>
    /// Queue a pre-UI marker together with its matching frame state. Both sides use <c>markerLock</c>, so
    /// the render thread cannot consume the native command before its state has been enqueued.
    /// </summary>
    public void QueuePreUiMarker(PreUiFrameState frameState)
        => QueueMarkerCore(BlurMarker, frameState);

    private void QueueMarkerCore(int sentinel, PreUiFrameState? preUiFrameState)
    {
        lock (markerLock)
        {
            if (!acceptingMarkers || !Available)
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

            // Ensure any managed allocation happens before the irreversible native queue operation. The render
            // thread blocks on markerLock until both the pending count and matching frame state are visible.
            if (preUiFrameState.HasValue)
                preUiFrameStates.EnsureCapacity(preUiFrameStates.Count + 1);

            context->PushBackCommand(command);
            pendingMarkers++;
            if (preUiFrameState.HasValue)
                preUiFrameStates.Enqueue(preUiFrameState.Value);
        }
    }

    public PreUiFrameState TakePreUiFrameState()
    {
        lock (markerLock)
            return preUiFrameStates.Count > 0
                ? preUiFrameStates.Dequeue()
                : PreUiFrameState.Create(skipEffect: false, effectOpacity: 1f);
    }

    public Task BeginDrain()
    {
        lock (markerLock)
        {
            acceptingMarkers = false;
            if (pendingMarkers == 0)
                return Task.CompletedTask;

            drainCompletion ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return drainCompletion.Task;
        }
    }

    public void MarkerConsumed()
    {
        TaskCompletionSource<bool>? completion = null;
        lock (markerLock)
        {
            if (pendingMarkers == 0)
                return;

            pendingMarkers--;
            if (pendingMarkers == 0)
                completion = drainCompletion;
        }

        completion?.TrySetResult(true);
    }

    public void Dispose()
    {
        lock (markerLock)
        {
            acceptingMarkers = false;
            disposed = true;
            preUiFrameStates.Clear();
        }
    }
}

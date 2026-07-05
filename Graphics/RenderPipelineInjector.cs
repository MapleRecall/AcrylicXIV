using System;
using System.Runtime.InteropServices;

using Dalamud.Game;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;

namespace AcrylicXIV.Graphics;

/// <summary>
/// Inserts a marker command into FFXIV's render command queue at a chosen point in the frame.
///
/// FFXIV records render commands on the main thread and replays them on the render thread. There is no
/// main-thread-safe way to touch the D3D immediate context, so to run our blur at a precise point in the frame
/// (right before the HUD is drawn) we instead <see cref="QueueBlurMarker"/> a dummy "set render target" command
/// with a sentinel <c>numRenderTargets</c> value into the queue from the main thread. When the render thread
/// replays the queue in order and reaches our marker, <see cref="RenderHooks"/> recognises the sentinel and does
/// the blur there — on the correct thread, once per frame, independent of how the scene was composited (so it is
/// robust to DLSS / frame-generation changing the per-frame render-target bind pattern).
///
/// This mechanism is ported from ffxiv-vr's RenderPipelineInjector, which uses the same technique to inject its
/// eye-copy commands.
/// </summary>
internal sealed unsafe class RenderPipelineInjector : IDisposable
{
    /// <summary>Sentinel <c>numRenderTargets</c> values marking our injected commands. Real binds use 1..8.</summary>
    internal const int BlurMarker = 0x1B70;
    internal const int PostUiMarker = 0x1B71;

    // Pushes a queued command onto the render command list for the current (recording) thread.
    private delegate void PushbackDelegate(ulong threadedOffset, ulong command);
    [Signature("E8 ?? ?? ?? ?? 0F 28 B4 24 A0 01 00 00 48 8B 8C 24 90 01 00 00", Fallibility = Fallibility.Fallible)]
    private readonly PushbackDelegate? pushback = null;

    // Allocates space for one command inside the current thread's render queue arena.
    private delegate ulong AllocateQueueMemoryDelegate(ulong threadedOffset, ulong size);
    [Signature("E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0f 84 ?? ?? ?? ?? 45 33 C0 41 BA 05 00 00 00", Fallibility = Fallibility.Fallible)]
    private readonly AllocateQueueMemoryDelegate? allocateQueueMemory = null;

    // Reads gs:[0x58] (the thread's TLS array pointer). Hand-assembled because C# can't emit a segment override.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong GetThreadedDataDelegate();

    private static readonly byte[] GetThreadedDataAsm =
    {
        0x55,                                                 // push rbp
        0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0x00, 0x00, 0x00, // mov rax, gs:[0x58]
        0x5D,                                                 // pop rbp
        0xC3,                                                 // ret
    };

    private const string TlsIndexSignature = "8B 0D ?? ?? ?? ?? 45 33 E4 41";

    private GCHandle asmHandle;
    private GetThreadedDataDelegate? getThreadedData;
    private nint tlsIndexAddress;
    private bool disposed;

    public RenderPipelineInjector(ISigScanner sigScanner, IGameInteropProvider gameInterop)
    {
        gameInterop.InitializeFromAttributes(this);

        try
        {
            tlsIndexAddress = sigScanner.GetStaticAddressFromSig(TlsIndexSignature);

            asmHandle = GCHandle.Alloc(GetThreadedDataAsm, GCHandleType.Pinned);
            var addr = asmHandle.AddrOfPinnedObject();
            var process = GetCurrentProcess();
            if (!VirtualProtectEx(process, addr, (nuint)GetThreadedDataAsm.Length, PAGE_EXECUTE_READWRITE, out _))
                throw new InvalidOperationException("VirtualProtectEx failed for GetThreadedData thunk.");
            if (!FlushInstructionCache(process, addr, (nuint)GetThreadedDataAsm.Length))
                throw new InvalidOperationException("FlushInstructionCache failed for GetThreadedData thunk.");

            getThreadedData = Marshal.GetDelegateForFunctionPointer<GetThreadedDataDelegate>(addr);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "RenderPipelineInjector setup failed; deterministic pre-UI blur unavailable.");
            getThreadedData = null;
        }
    }

    /// <summary>True if every piece needed to queue a marker resolved on this client.</summary>
    public bool Available => pushback != null && allocateQueueMemory != null && getThreadedData != null && tlsIndexAddress != 0;

    /// <summary>
    /// Queue a sentinel marker into the current thread's render command list. Must be called from the thread
    /// that records render commands (i.e. from inside a main-thread render hook such as the UI pushback), so the
    /// marker lands in that thread's queue and is replayed in frame order.
    /// </summary>
    public void QueueMarker(int sentinel)
    {
        if (!Available || disposed)
            return;

        var threadedOffset = GetThreadedOffset();
        if (threadedOffset == 0)
            return;

        var command = (SetRenderTargetCommand*)allocateQueueMemory!(threadedOffset, (ulong)sizeof(SetRenderTargetCommand));
        if (command == null)
            return;

        *command = default;
        command->SwitchType = 0;
        command->numRenderTargets = sentinel;
        command->RenderTarget0 = null;

        pushback!(threadedOffset, (ulong)command);
    }

    private ulong GetThreadedOffset()
    {
        var threadedData = getThreadedData!();
        if (threadedData == 0)
            return 0;

        threadedData = *(ulong*)(threadedData + (ulong)(*(int*)tlsIndexAddress * 8));
        if (threadedData == 0)
            return 0;

        return *(ulong*)(threadedData + 0x238);
    }

    public void Dispose()
    {
        disposed = true;
        getThreadedData = null;
        if (asmHandle.IsAllocated)
            asmHandle.Free();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(nint hProcess, nint address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint hProcess, nint address, nuint size);

    private const uint PAGE_EXECUTE_READWRITE = 0x40;
}

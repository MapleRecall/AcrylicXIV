using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace AcrylicXIV.Graphics;

/// <summary>
/// The argument the game's render thread passes to its "set render target" dispatch. Layout mirrors
/// ffxiv-vr's SetRenderTargetCommand (verified against CS's RenderCommandSetTarget): a switch/type word,
/// a render-target count, up to five colour targets, then a depth buffer.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x38)]
internal unsafe struct SetRenderTargetCommand
{
    [FieldOffset(0x00)] public int SwitchType;
    [FieldOffset(0x04)] public int numRenderTargets;
    [FieldOffset(0x08)] public Texture* RenderTarget0;
    [FieldOffset(0x10)] public Texture* RenderTarget1;
    [FieldOffset(0x18)] public Texture* RenderTarget2;
    [FieldOffset(0x20)] public Texture* RenderTarget3;
    [FieldOffset(0x28)] public Texture* RenderTarget4;
    [FieldOffset(0x30)] public Texture* DepthBuffer;
}

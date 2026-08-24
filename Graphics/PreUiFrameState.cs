using System;
using System.Runtime.CompilerServices;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;

using NumericsVector4 = System.Numerics.Vector4;

namespace AcrylicXIV.Graphics;

/// <summary>
/// Main-thread state paired with one pre-UI render marker. The fixed inline buffer keeps the per-frame handoff
/// allocation-free and safe to copy through the marker queue.
/// </summary>
internal unsafe struct PreUiFrameState
{
    internal const int MaxExcludedRegions = 3;
    private const float Padding = 3f;

    [InlineArray(MaxExcludedRegions)]
    private struct RectangleBuffer
    {
        private NumericsVector4 element0;
    }

    private RectangleBuffer rectangles;

    public int Count { get; private set; }
    public float EffectOpacity { get; private set; }
    public bool SkipEffect { get; private set; }

    public static PreUiFrameState Create(bool skipEffect, float effectOpacity) => new()
    {
        SkipEffect = skipEffect,
        EffectOpacity = Math.Clamp(effectOpacity, 0f, 1f),
    };

    public static PreUiFrameState Capture(IGameGui gameGui, float effectOpacity = 1f)
    {
        var state = Create(skipEffect: false, effectOpacity: effectOpacity);

        AtkResNode* nameplateNode = null;
        var nameplateAddon = gameGui.GetAddonByName<AddonNamePlate>("NamePlate");
        var uiModule = UIModule.Instance();
        var ui3DModule = uiModule == null ? null : uiModule->GetUI3DModule();
        var targetInfo = ui3DModule == null ? null : ui3DModule->TargetObjectInfo;
        if (nameplateAddon != null && nameplateAddon->NamePlateObjectArray != null && targetInfo != null &&
            targetInfo->NamePlateIndex < AddonNamePlate.NumNamePlateObjects)
        {
            nameplateNode = (AtkResNode*)nameplateAddon->NamePlateObjectArray[targetInfo->NamePlateIndex]
                .RootComponentNode;
        }

        AtkResNode* cursorNode3 = null;
        AtkResNode* cursorNode4 = null;
        var targetCursorAddon = gameGui.GetAddonByName<AtkUnitBase>("_TargetCursor");
        if (targetCursorAddon != null && targetCursorAddon->IsVisible)
        {
            // Nodes 3 and 4 own the two visible animated cursor pieces. The root and node 2 are positioning
            // containers whose bounds are too broad for exclusion.
            cursorNode3 = targetCursorAddon->GetNodeById(3);
            cursorNode4 = targetCursorAddon->GetNodeById(4);
        }

        state.TryAddBounds(nameplateNode);
        state.TryAddBounds(cursorNode3);
        state.TryAddBounds(cursorNode4);
        return state;
    }

    public readonly int CopyRectangles(Span<NumericsVector4> destination)
    {
        var count = Math.Min(Count, destination.Length);
        for (var i = 0; i < count; i++)
            destination[i] = rectangles[i];
        return count;
    }

    private void TryAddBounds(AtkResNode* node)
    {
        if (node == null || !node->IsVisible() || Count >= MaxExcludedRegions)
            return;

        Bounds bounds;
        node->GetBounds(&bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        rectangles[Count++] = new NumericsVector4(
            bounds.Pos1.X - Padding,
            bounds.Pos1.Y - Padding,
            bounds.Pos2.X + Padding,
            bounds.Pos2.Y + Padding);
    }
}

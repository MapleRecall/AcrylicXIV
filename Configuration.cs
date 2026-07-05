using System;
using System.Numerics;
using Dalamud.Configuration;

namespace AcrylicXIV;

/// <summary>Which blur kernel to use for the backdrop.</summary>
public enum BlurAlgorithm
{
    /// <summary>Separable Gaussian at 1/4 resolution. Smooth, weighted, no grid artefacts.</summary>
    Gaussian = 1,

    /// <summary>Dual Kawase (downsample/upsample pyramid), like Dalamud's ImGui blur. Smoothest + cheapest for large blur.</summary>
    Kawase = 2,
}

/// <summary>Plugin UI language. <see cref="Auto"/> follows Dalamud's own UI language setting.</summary>
public enum PluginLanguage
{
    Auto = 0,
    English = 1,
    ChineseSimplified = 2,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Master switch for the blur injection. On by default (this is the tuned default configuration). All GPU
    /// work runs on the game's render thread, so it can't race the renderer.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Plugin UI language. Auto follows Dalamud's own UI language setting.</summary>
    public PluginLanguage Language { get; set; } = PluginLanguage.Auto;

    /// <summary>
    /// Debug: apply the effect to the whole screen, ignoring the UI coverage mask (the compose pass is skipped),
    /// so the raw blur — or a flat colour when <see cref="DebugClear"/> is on — is visible everywhere.
    /// </summary>
    public bool FullscreenTest { get; set; } = false;

    /// <summary>
    /// Sub-option of <see cref="FullscreenTest"/>: fill the screen with a flat colour instead of blurring. If the
    /// HUD still draws crisply on top of the flat colour, the injection point (before the HUD pass) is confirmed.
    /// </summary>
    public bool DebugClear { get; set; } = false;

    /// <summary>
    /// UI alpha at/below which the background stays fully sharp (no blur). Below <see cref="MaskAlphaEnd"/> the
    /// background blur ramps in linearly. Mirrors the ReShade KeepUIX "background blur start alpha".
    /// </summary>
    public float MaskAlphaStart { get; set; } = 0.1f;

    /// <summary>
    /// UI alpha at/above which the background is fully blurred. Between <see cref="MaskAlphaStart"/> and this the
    /// blur ramps in. Mirrors the ReShade KeepUIX "background blur end alpha".
    /// </summary>
    public float MaskAlphaEnd { get; set; } = 0.2f;

    /// <summary>
    /// When the UI covers the whole screen — e.g. a full-screen map/menu/loading overlay — disable the background
    /// blur (show it sharp). Avoids the disorienting full-screen blur→sharp flash when such a cover appears or
    /// disappears. Detection is "no fully-transparent hole anywhere on screen", so normal HUDs never trigger it.
    /// </summary>
    public bool SkipFullscreenUi { get; set; } = false;

    /// <summary>Enable the frosted-glass blur behind the UI. Off keeps only the material effects over the sharp scene.</summary>
    public bool BlurEnabled { get; set; } = true;

    /// <summary>
    /// Diagnostic for the "blur only under the UI" work. When on, the pre-UI marker resets the back-buffer alpha
    /// to 0 and the post-UI marker overwrites the frame with that alpha channel as greyscale. If the native UI
    /// shows up (white where UI is), FFXIV writes per-pixel UI coverage into the back-buffer alpha, which we can
    /// then use to mask the blur. If the screen stays black, that channel is unusable and we need another source.
    /// </summary>
    public bool DebugShowUiMask { get; set; } = false;

    /// <summary>Which blur kernel to use. Kawase (dual downsample/upsample) is the smoothest and cheapest.</summary>
    public BlurAlgorithm BlurAlgorithm { get; set; } = BlurAlgorithm.Kawase;

    /// <summary>
    /// Gaussian only. How many times to halve the resolution before blurring (0 = full res, 1 = 1/2, 2 = 1/4,
    /// ...). This is the downsample ratio (2^levels): higher = softer base blur and much cheaper. Kawase ignores
    /// this (its own strength drives the downsampling).
    /// </summary>
    public int DownsampleLevels { get; set; } = 2;

    /// <summary>
    /// Kawase blur strength (0 = no blur). Kawase blurs BY its downsample/upsample pyramid, so this single value
    /// is both the size and the (automatic, smooth) downsample. Independent from <see cref="GaussianStrength"/>
    /// so switching algorithms never re-tunes the other one.
    /// </summary>
    public float KawaseStrength { get; set; } = 4.0f;

    /// <summary>
    /// Gaussian kernel strength, applied on top of <see cref="DownsampleLevels"/> (0 = only the downsample
    /// softening). Independent from <see cref="KawaseStrength"/> so switching algorithms keeps each one's tuning.
    /// </summary>
    public float GaussianStrength { get; set; } = 4.0f;

    // --- Frosted-glass / acrylic material. Each effect has an explicit on/off toggle; its strength applies only
    //     while enabled. The material only touches the blurred background visible through semi-transparent UI. ---

    /// <summary>Enable the grain (noise) micro-texture over the frosted background.</summary>
    public bool GrainEnabled { get; set; } = true;

    /// <summary>Grain amount while enabled.</summary>
    public float GrainAmount { get; set; } = 0.02f;

    /// <summary>Grain cell size in pixels (larger = coarser grain).</summary>
    public float GrainScale { get; set; } = 1.5f;

    /// <summary>Grain style: soft interpolated value-noise (frost) when true, sharp quantized cells when false.</summary>
    public bool GrainSoft { get; set; } = false;

    /// <summary>Enable tinting the frosted background toward <see cref="TintColor"/>.</summary>
    public bool TintEnabled { get; set; } = false;

    /// <summary>Tint colour lerped into the frosted background (acrylic's coloured-glass layer).</summary>
    public Vector3 TintColor { get; set; } = new(0.48601016f, 0.69316584f, 0.8069164f);

    /// <summary>Tint strength while enabled.</summary>
    public float TintAmount { get; set; } = 0.5f;

    /// <summary>Enable Distortion (glass warp) of the frosted background.</summary>
    public bool DistortEnabled { get; set; } = false;

    /// <summary>Distortion strength in pixels while enabled.</summary>
    public float DistortAmount { get; set; } = 8.0f;

    /// <summary>Distortion pattern scale in pixels (larger = broader, smoother ripples).</summary>
    public float DistortScale { get; set; } = 2.0f;

    /// <summary>Enable brightness/saturation/contrast grading of the frosted background.</summary>
    public bool AdjustEnabled { get; set; } = false;

    /// <summary>Brightness multiplier for the frosted background (1 = unchanged).</summary>
    public float Brightness { get; set; } = 1.25f;

    /// <summary>Saturation of the frosted background (0 = greyscale, 1 = unchanged, &gt;1 = boosted).</summary>
    public float Saturation { get; set; } = 1.5f;

    /// <summary>Contrast of the frosted background (1 = unchanged).</summary>
    public float Contrast { get; set; } = 1.25f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using AcrylicXIV.Localization;

namespace AcrylicXIV.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration config;

    public ConfigWindow(Plugin plugin)
        : base("AcrylicXIV###acrylicxiv-config")
    {
        Size = new Vector2(450, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        config = plugin.Configuration;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        // Keep the visible title localized; the ###id part keeps the window identity (and its position) stable.
        WindowName = Loc.Get("WindowTitle") + "###acrylicxiv-config";

        var enabled = config.Enabled;
        if (ImGui.Checkbox(Loc.Get("Enabled"), ref enabled))
        {
            config.Enabled = enabled;
            config.Save();
        }

        if (ImGui.BeginTabBar("acrylic-tabs"))
        {
            if (ImGui.BeginTabItem(Loc.Get("TabGeneral") + "###general"))
            {
                DrawGeneralTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Loc.Get("TabBlurTuner") + "###tuner"))
            {
                DrawBlurTunerTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Loc.Get("TabDebug") + "###debug"))
            {
                DrawDebugTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    // Language + coverage: which UI gets a blurred backdrop, and when to skip it.
    private void DrawGeneralTab()
    {
        ImGui.Spacing();

        var langIdx = (int)config.Language;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.Combo(Loc.Get("Language") + "###language", ref langIdx, Loc.Get("LangAuto") + "\0English\0中文\0"))
        {
            config.Language = (PluginLanguage)Math.Clamp(langIdx, 0, 2);
            config.Save();
            Loc.Apply(config.Language);
        }

        ImGui.Separator();

        var skipFs = config.SkipFullscreenUi;
        if (ImGui.Checkbox(Loc.Get("SkipFullscreen"), ref skipFs))
        {
            config.SkipFullscreenUi = skipFs;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("SkipFullscreenHelp"));

        var start = config.MaskAlphaStart;
        if (ImGui.SliderFloat(Loc.Get("BlurStartAlpha"), ref start, 0.0f, 1.0f, "%.2f"))
        {
            config.MaskAlphaStart = start;
            // The two thresholds push each other so they never cross (start <= full).
            if (config.MaskAlphaEnd < start)
                config.MaskAlphaEnd = start;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("BlurStartAlphaHelp"));

        var end = config.MaskAlphaEnd;
        if (ImGui.SliderFloat(Loc.Get("FullBlurAlpha"), ref end, 0.0f, 1.0f, "%.2f"))
        {
            config.MaskAlphaEnd = end;
            // The two thresholds push each other so they never cross (full >= start).
            if (config.MaskAlphaStart > end)
                config.MaskAlphaStart = end;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("FullBlurAlphaHelp"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushTextWrapPos(0.0f);
        ImGui.TextUnformatted(Loc.Get("InterWindowNote"));
        ImGui.PopTextWrapPos();
    }

    // The blur algorithm and its tuning, plus the frosted-glass material layered on top.
    private void DrawBlurTunerTab()
    {
        ImGui.Spacing();

        // --- Blur ---
        var blurEnabled = config.BlurEnabled;
        if (ImGui.Checkbox(Loc.Get("Blur"), ref blurEnabled))
        {
            config.BlurEnabled = blurEnabled;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("BlurHelp"));

        if (config.BlurEnabled)
        {
            ImGui.Indent();

            var gaussianSel = config.BlurAlgorithm == BlurAlgorithm.Gaussian;
            if (ImGui.RadioButton(Loc.Get("AlgoKawase"), !gaussianSel))
            {
                config.BlurAlgorithm = BlurAlgorithm.Kawase;
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.Get("AlgoGaussian"), gaussianSel))
            {
                config.BlurAlgorithm = BlurAlgorithm.Gaussian;
                config.Save();
            }
            ImGui.SameLine();
            HelpMarker(Loc.Get("AlgoHelp"));

            if (config.BlurAlgorithm == BlurAlgorithm.Gaussian)
            {
                var downsample = config.DownsampleLevels;
                if (ImGui.SliderInt(Loc.Get("Downsample"), ref downsample, 0, 5, DownsampleLabel(downsample)))
                {
                    config.DownsampleLevels = Math.Clamp(downsample, 0, 5);
                    config.Save();
                }
                ImGui.SameLine();
                HelpMarker(Loc.Get("DownsampleHelp"));

                var gStrength = config.GaussianStrength;
                if (ImGui.SliderFloat(Loc.Get("BlurStrength"), ref gStrength, 0.0f, 8.0f, "%.1f"))
                {
                    config.GaussianStrength = gStrength;
                    config.Save();
                }
            }
            else
            {
                var kStrength = config.KawaseStrength;
                if (ImGui.SliderFloat(Loc.Get("BlurStrength"), ref kStrength, 0.0f, 8.0f, "%.1f"))
                {
                    config.KawaseStrength = kStrength;
                    config.Save();
                }
            }

            ImGui.Unindent();
        }

        ImGui.Separator();

        // --- Grain ---
        var grainEnabled = config.GrainEnabled;
        if (ImGui.Checkbox(Loc.Get("Grain"), ref grainEnabled))
        {
            config.GrainEnabled = grainEnabled;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("GrainHelp"));

        if (config.GrainEnabled)
        {
            ImGui.Indent();

            var grain = config.GrainAmount;
            if (ImGui.SliderFloat(Loc.Get("EffectAmount") + "###grainAmt", ref grain, 0.0f, 0.2f, "%.3f"))
            {
                config.GrainAmount = grain;
                config.Save();
            }

            var grainScale = config.GrainScale;
            if (ImGui.SliderFloat(Loc.Get("GrainSize"), ref grainScale, 1.0f, 8.0f, "%.1f"))
            {
                config.GrainScale = grainScale;
                config.Save();
            }

            var grainSoft = config.GrainSoft;
            if (ImGui.Checkbox(Loc.Get("GrainSoft"), ref grainSoft))
            {
                config.GrainSoft = grainSoft;
                config.Save();
            }
            ImGui.SameLine();
            HelpMarker(Loc.Get("GrainSoftHelp"));

            ImGui.Unindent();
        }

        ImGui.Separator();

        // --- Distortion ---
        var DistortEnabled = config.DistortEnabled;
        if (ImGui.Checkbox(Loc.Get("Distortion"), ref DistortEnabled))
        {
            config.DistortEnabled = DistortEnabled;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("DistortionHelp"));

        if (config.DistortEnabled)
        {
            ImGui.Indent();

            var Distort = config.DistortAmount;
            if (ImGui.SliderFloat(Loc.Get("EffectAmount") + "###DistortAmt", ref Distort, 0.0f, 20.0f, "%.1f"))
            {
                config.DistortAmount = Distort;
                config.Save();
            }

            var DistortScale = config.DistortScale;
            if (ImGui.SliderFloat(Loc.Get("DistortionScale"), ref DistortScale, 2.0f, 40.0f, "%.0f"))
            {
                config.DistortScale = DistortScale;
                config.Save();
            }
            ImGui.SameLine();
            HelpMarker(Loc.Get("DistortionScaleHelp"));

            ImGui.Unindent();
        }

        ImGui.Separator();

        // --- Tint ---
        var tintEnabled = config.TintEnabled;
        if (ImGui.Checkbox(Loc.Get("Tint"), ref tintEnabled))
        {
            config.TintEnabled = tintEnabled;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("TintHelp"));

        if (config.TintEnabled)
        {
            ImGui.Indent();

            var tintAmount = config.TintAmount;
            if (ImGui.SliderFloat(Loc.Get("EffectAmount") + "###tintAmt", ref tintAmount, 0.0f, 1.0f, "%.2f"))
            {
                config.TintAmount = tintAmount;
                config.Save();
            }

            var tint = config.TintColor;
            if (ImGui.ColorEdit3(Loc.Get("TintColour"), ref tint))
            {
                config.TintColor = tint;
                config.Save();
            }

            ImGui.Unindent();
        }

        ImGui.Separator();

        // --- Background adjust ---
        var adjustEnabled = config.AdjustEnabled;
        if (ImGui.Checkbox(Loc.Get("Adjust"), ref adjustEnabled))
        {
            config.AdjustEnabled = adjustEnabled;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("AdjustHelp"));

        if (config.AdjustEnabled)
        {
            ImGui.Indent();

            var brightness = config.Brightness;
            if (ImGui.SliderFloat(Loc.Get("Brightness"), ref brightness, 0.0f, 2.0f, "%.2f"))
            {
                config.Brightness = brightness;
                config.Save();
            }

            var saturation = config.Saturation;
            if (ImGui.SliderFloat(Loc.Get("Saturation"), ref saturation, 0.0f, 2.0f, "%.2f"))
            {
                config.Saturation = saturation;
                config.Save();
            }

            var contrast = config.Contrast;
            if (ImGui.SliderFloat(Loc.Get("Contrast"), ref contrast, 0.0f, 2.0f, "%.2f"))
            {
                config.Contrast = contrast;
                config.Save();
            }

            ImGui.Unindent();
        }
    }

    // Diagnostics.
    private void DrawDebugTab()
    {
        ImGui.Spacing();

        var fullscreen = config.FullscreenTest;
        if (ImGui.Checkbox(Loc.Get("FullscreenTest"), ref fullscreen))
        {
            config.FullscreenTest = fullscreen;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("FullscreenTestHelp"));

        if (config.FullscreenTest)
        {
            ImGui.Indent();

            var debugClear = config.DebugClear;
            if (ImGui.Checkbox(Loc.Get("DebugClear"), ref debugClear))
            {
                config.DebugClear = debugClear;
                config.Save();
            }
            ImGui.SameLine();
            HelpMarker(Loc.Get("DebugClearHelp"));

            ImGui.Unindent();
        }

        var showMask = config.DebugShowUiMask;
        if (ImGui.Checkbox(Loc.Get("DebugMask"), ref showMask))
        {
            config.DebugShowUiMask = showMask;
            config.Save();
        }
        ImGui.SameLine();
        HelpMarker(Loc.Get("DebugMaskHelp"));
    }

    private static string DownsampleLabel(int levels) => levels switch
    {
        <= 0 => Loc.Get("DownsampleFull"),
        1 => "1/2",
        2 => "1/4",
        3 => "1/8",
        4 => "1/16",
        _ => "1/32",
    };

    private static void HelpMarker(string text)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32.0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}

using System;
using System.Globalization;
using System.Resources;

namespace AcrylicXIV.Localization;

/// <summary>
/// Minimal native localization. Two neutral <see cref="ResourceManager"/>s (English + Simplified Chinese) are both
/// embedded in the main plugin assembly (the .resx files carry no culture suffix, so the build produces no
/// satellite assemblies). This matters because Dalamud loads each plugin in its own AssemblyLoadContext, which
/// does not resolve satellite assemblies — embedding every language in the main DLL sidesteps that entirely.
///
/// The active language follows the plugin's <see cref="PluginLanguage"/> setting, falling back to Dalamud's own
/// UI language when set to <see cref="PluginLanguage.Auto"/>.
/// </summary>
internal static class Loc
{
    private static readonly ResourceManager En =
        new("AcrylicXIV.Localization.StringsEn", typeof(Loc).Assembly);

    private static readonly ResourceManager Zh =
        new("AcrylicXIV.Localization.StringsZh", typeof(Loc).Assembly);

    private static ResourceManager active = En;

    /// <summary>Select the active language from the plugin setting (Auto follows Dalamud's UI language).</summary>
    public static void Apply(PluginLanguage language)
    {
        var code = language switch
        {
            PluginLanguage.English => "en",
            PluginLanguage.ChineseSimplified => "zh",
            _ => Plugin.PluginInterface.UiLanguage,
        };
        active = (code ?? "en").StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Zh : En;
    }

    /// <summary>
    /// Localized string for <paramref name="key"/>. Reads the neutral resource with the invariant culture (so it
    /// never probes for a satellite), falling back to English and finally the key itself.
    /// </summary>
    public static string Get(string key)
        => active.GetString(key, CultureInfo.InvariantCulture)
           ?? En.GetString(key, CultureInfo.InvariantCulture)
           ?? key;
}

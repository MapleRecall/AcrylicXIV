using System;

using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using AcrylicXIV.Graphics;
using AcrylicXIV.Localization;
using AcrylicXIV.Windows;

namespace AcrylicXIV;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/acrylic";

    private readonly BackdropBlurRenderer renderer = new();
    private readonly RenderPipelineInjector injector;
    private readonly RenderHooks renderHooks;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("AcrylicXIV");
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Resolve the UI language before anything reads a localized string (Auto follows Dalamud's own setting).
        Loc.Apply(Configuration.Language);
        PluginInterface.LanguageChanged += OnDalamudLanguageChanged;

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.Get("CommandHelp"),
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        // All D3D work happens on the game render thread inside RenderHooks — never from the main thread.
        injector = new RenderPipelineInjector(SigScanner, GameInterop);
        renderHooks = new RenderHooks(renderer, () => Configuration, injector, GameInterop);

        if (renderHooks.DeterministicReady)
            Log.Information("Deterministic pre-UI blur live. Blur is {State}.", Configuration.Enabled ? "ENABLED" : "disabled (opt in via /acrylic)");
        else if (renderHooks.Available)
            Log.Warning("Render-thread hook live but pushback/injector signatures did not fully resolve; blur may be inactive on this client.");
        else
            Log.Warning("Render-thread signature not found on this client; blur will be inactive.");
    }

    public void Dispose()
    {
        PluginInterface.LanguageChanged -= OnDalamudLanguageChanged;
        renderHooks.Dispose();
        injector.Dispose();
        renderer.Dispose();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;
        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    // "/acrylic" opens settings; "/acrylic on|off|toggle" flips the master switch without opening the window.
    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                SetEnabled(true);
                break;
            case "off":
                SetEnabled(false);
                break;
            case "toggle":
                SetEnabled(!Configuration.Enabled);
                break;
            default:
                ToggleConfigUi();
                break;
        }
    }

    private void SetEnabled(bool enabled)
    {
        Configuration.Enabled = enabled;
        Configuration.Save();
        ChatGui.Print(Loc.Get(enabled ? "CmdOn" : "CmdOff"));
    }

    // When the user has the plugin language on Auto, follow Dalamud's UI language as it changes.
    private void OnDalamudLanguageChanged(string langCode)
    {
        if (Configuration.Language == PluginLanguage.Auto)
            Loc.Apply(PluginLanguage.Auto);
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}

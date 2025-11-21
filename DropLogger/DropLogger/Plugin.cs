using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DropLogger.Windows;
using DropLogger.Logic;
using System;

namespace DropLogger
{
    public sealed class Plugin : IDalamudPlugin
    {
        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] internal static IChatGui Chat { get; private set; } = null!;
        [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
        [PluginService] internal static IClientState ClientState { get; private set; } = null!;
        [PluginService] internal static IDataManager Data { get; private set; } = null!;

        private const string _commandName = "/droplog";
        private const string _commandHelpMessage = "Opens the DropLogger configuration window.";

        public Config Configuration { get; init; }
        public readonly WindowSystem WindowSystem = new("DropLogger");

        private ConfigWindow ConfigWindow { get; init; }
        private DropTracker DropTracker { get; init; }

        public Plugin()
        {
            Configuration = PluginInterface.GetPluginConfig() as Config ?? new Config();
            Configuration.Initialize(PluginInterface);

            DropTracker = new DropTracker(Configuration, Chat, ClientState, PluginLog, Data);

            ConfigWindow = new ConfigWindow(Configuration);
            WindowSystem.AddWindow(ConfigWindow);

            CommandManager.AddHandler(_commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = _commandHelpMessage
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUI;

            PluginLog.Info("DropLogger initialized successfully.");
        }

        public void Dispose()
        {
            WindowSystem.RemoveAllWindows();
            CommandManager.RemoveHandler(_commandName);

            DropTracker.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            ToggleConfigUI();
        }

        private void DrawUI() => WindowSystem.Draw();
        public void ToggleConfigUI() => ConfigWindow.Toggle();
    }
}
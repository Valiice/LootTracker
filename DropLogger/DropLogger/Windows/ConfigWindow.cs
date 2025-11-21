using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace DropLogger.Windows
{
    public class ConfigWindow : Window, IDisposable
    {
        private readonly Config _config;

        public ConfigWindow(Config config) : base("Drop Logger Configuration")
        {
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(300, 200),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
            _config = config;
        }

        public void Dispose() { GC.SuppressFinalize(this); }

        public override void Draw()
        {
            ImGui.Text("Main Settings");
            ImGui.Separator();
            ImGui.Spacing();

            var loggingEnabled = _config.IsLoggingEnabled;
            if (ImGui.Checkbox("Enable Drop Logging", ref loggingEnabled))
            {
                _config.IsLoggingEnabled = loggingEnabled;
                _config.Save();
            }

            var debugEnabled = _config.EnableDebugLogging;
            if (ImGui.Checkbox("Enable Debug Logging", ref debugEnabled))
            {
                _config.EnableDebugLogging = debugEnabled;
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Prints detailed logs to /xllog");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text("Advanced Settings");

            var apiUrl = _config.ApiUrl;
            ImGui.SetNextItemWidth(300);
            if (ImGui.InputText("API URL", ref apiUrl, 200))
            {
                _config.ApiUrl = apiUrl;
                _config.Save();
            }

            var bufferSize = _config.BufferSize;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Buffer Size", ref bufferSize))
            {
                if (bufferSize < 1) bufferSize = 1;
                _config.BufferSize = bufferSize;
                _config.Save();
            }
        }
    }
}
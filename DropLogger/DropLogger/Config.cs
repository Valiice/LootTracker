using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace DropLogger
{
    [Serializable]
    public class Config : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        public bool IsLoggingEnabled { get; set; } = true;
        public bool EnableDebugLogging { get; set; } = false;
        public string ApiUrl { get; set; } = "http://localhost:3000/api/v1/submit";

        public int BufferSize { get; set; } = 20;

        [NonSerialized]
        private IDalamudPluginInterface? _pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;
        }

        public void Save()
        {
            _pluginInterface!.SavePluginConfig(this);
        }
    }
}
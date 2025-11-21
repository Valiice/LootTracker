using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DropLogger.Logic
{
    public class DropTracker : IDisposable
    {
        private readonly Config _config;
        private readonly IChatGui _chat;
        private readonly IClientState _clientState;
        private readonly IPluginLog _pluginLog;
        private readonly IDataManager _data;

        private static readonly HttpClient _httpClient = new();
        private readonly Regex _dropRegex = new(@"You obtain (an? )?(\d+ )?(.+?)\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly Regex _killRegex = new(@"You defeat the (.+?)\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly List<DropData> _dropBuffer = [];
        private DateTime _lastUploadTime = DateTime.UtcNow;

        private readonly Lock _bufferLock = new();

        private string _lastKilledMob = "Unknown";
        private int _lastKilledMobId = 0;
        private DateTime _lastKillTime = DateTime.MinValue;

        public DropTracker(Config config, IChatGui chat, IClientState clientState, IPluginLog pluginLog, IDataManager data)
        {
            _config = config;
            _chat = chat;
            _clientState = clientState;
            _pluginLog = pluginLog;
            _data = data;

            _chat.ChatMessage += OnChatMessage;
        }

        public void Dispose()
        {
            _chat.ChatMessage -= OnChatMessage;

            lock (_bufferLock)
            {
                if (_dropBuffer.Count > 0)
                {
                    try
                    {
                        Task.Run(() => UploadBufferAsync()).Wait(2000);
                    }
                    catch (Exception ex) { _pluginLog.Error(ex, "Failed to upload on shutdown"); }
                }
            }

            GC.SuppressFinalize(this);
        }

        private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            if (!_config.IsLoggingEnabled) return;

            var msgText = message.ToString();

            var killMatch = _killRegex.Match(msgText);
            if (killMatch.Success)
            {
                HandleKill(killMatch);
                return;
            }

            var dropMatch = _dropRegex.Match(msgText);
            if (dropMatch.Success)
            {
                HandleDrop(dropMatch, message);
                return;
            }
        }

        private void HandleKill(Match match)
        {
            try
            {
                string mobName = match.Groups[1].Value;
                uint mobId = 0;
                string officialName = mobName;

                var mobSheet = _data.GetExcelSheet<BNpcName>();
                if (mobSheet != null)
                {
                    var mobRow = mobSheet.FirstOrDefault(x => x.Singular.ToString().Equals(mobName, StringComparison.OrdinalIgnoreCase));

                    if (mobRow.RowId != 0)
                    {
                        mobId = mobRow.RowId;
                        officialName = mobRow.Singular.ToString();
                    }
                }

                _lastKilledMob = officialName;
                _lastKilledMobId = (int)mobId;
                _lastKillTime = DateTime.UtcNow;

                LogAndBuffer($"MOB-{officialName}", (int)mobId, 1, false, true);
            }
            catch (Exception ex) { _pluginLog.Error(ex, "Error parsing kill"); }
        }

        private void HandleDrop(Match match, SeString message)
        {
            try
            {
                string quantityStr = match.Groups[2].Value;
                int quantity = 1;
                if (!string.IsNullOrWhiteSpace(quantityStr)) int.TryParse(quantityStr.Trim(), out quantity);

                bool isHq = match.Groups[3].Value.Contains('');
                string cleanName = match.Groups[3].Value.Replace("", "").Trim();
                string lowerName = cleanName.ToLower();

                int itemId = 0;

                if (message.Payloads.FirstOrDefault(p => p is ItemPayload) is ItemPayload itemPayload)
                {
                    itemId = (int)itemPayload.ItemId;

                    if ((itemId > 0 && itemId < 100) || itemId == 21072)
                    {
                        _pluginLog.Debug($"[Ignored] Currency ID: {cleanName} ({itemId})");
                        return;
                    }

                    var itemSheet = _data.GetExcelSheet<Item>();
                    if (itemSheet != null && itemSheet.TryGetRow((uint)itemId, out var itemRow))
                    {
                        if (itemRow.ItemUICategory.Value.RowId == 63)
                        {
                            _pluginLog.Debug($"[Ignored] Key Item: {cleanName} ({itemId})");
                            return;
                        }

                        cleanName = itemRow.Name.ToString();
                    }
                }
                else
                {
                    if (lowerName.Contains("gil") ||
                        lowerName.Contains("tomestone") ||
                        lowerName.Contains("venture") ||
                        lowerName.Contains("seal") ||
                        lowerName.Contains("materia"))
                    {
                        _pluginLog.Debug($"[Ignored] Currency Text: {cleanName}");
                        return;
                    }
                }

                string sourceMob = "Unknown";
                int sourceMobId = 0;

                if ((DateTime.UtcNow - _lastKillTime).TotalSeconds < 5)
                {
                    sourceMob = _lastKilledMob;
                    sourceMobId = _lastKilledMobId;
                }

                LogAndBuffer(cleanName, itemId, quantity, isHq, false, sourceMob, sourceMobId);
            }
            catch (Exception ex) { _pluginLog.Error(ex, "Error parsing drop"); }
        }

        private void LogAndBuffer(string name, int itemId, int quantity, bool isHq, bool isMob, string sourceMob = null, int sourceMobId = 0)
        {
            ushort zoneId = _clientState.TerritoryType;
            string userHash = _clientState.LocalContentId == 0 ? "000000" : _clientState.LocalContentId.ToString("X");

            if (_config.EnableDebugLogging)
            {
                _pluginLog.Debug($"[Tracking] {quantity}x {name} (ID: {itemId} | Zone: {zoneId})");
            }

            AddToBuffer(new DropData
            {
                ZoneID = zoneId,
                ItemName = name,
                ItemID = itemId,
                Quantity = quantity,
                IsHQ = isHq,
                UserHash = userHash,
                SourceMob = isMob ? null : sourceMob,
                SourceMobID = isMob ? null : (int?)sourceMobId
            });
        }

        private void AddToBuffer(DropData data)
        {
            lock (_bufferLock)
            {
                _dropBuffer.Add(data);
                if (_dropBuffer.Count >= _config.BufferSize || (DateTime.UtcNow - _lastUploadTime).TotalMinutes > 5)
                {
                    _ = Task.Run(UploadBufferAsync);
                }
            }
        }

        private async Task UploadBufferAsync()
        {
            List<DropData> batch;
            lock (_bufferLock)
            {
                if (_dropBuffer.Count == 0) return;
                batch = new List<DropData>(_dropBuffer);
                _dropBuffer.Clear();
                _lastUploadTime = DateTime.UtcNow;
            }

            try
            {
                var json = JsonConvert.SerializeObject(batch);
                if (_config.EnableDebugLogging) _pluginLog.Debug($"[Payload] {json}");

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DropLogger-Plugin/1.0");

                var response = await _httpClient.PostAsync(_config.ApiUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    _pluginLog.Error($"Upload failed: {response.StatusCode}");
                }
                else if (_config.EnableDebugLogging)
                {
                    _pluginLog.Info($"Synced {batch.Count} events.");
                }
            }
            catch (Exception ex) { _pluginLog.Error(ex, "Upload exception"); }
        }

        public class DropData
        {
            public ushort ZoneID { get; set; }
            public string ItemName { get; set; }
            public int ItemID { get; set; }
            public int Quantity { get; set; }
            public bool IsHQ { get; set; }
            public string UserHash { get; set; }
            public string SourceMob { get; set; }
            public int? SourceMobID { get; set; }
        }
    }
}
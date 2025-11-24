using Dalamud.Plugin.Services;
using LuminaItem = Lumina.Excel.Sheets.Item;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;

namespace DropLogger.Logic
{
    public class DropTracker : IDisposable
    {
        private readonly Config _config;
        private readonly IClientState _clientState;
        private readonly IPluginLog _pluginLog;
        private readonly IDataManager _data;
        private readonly IObjectTable _objectTable;
        private readonly IFramework _framework;
        private readonly IGameGui _gameGui;
        private readonly IPartyList _partyList;

        private static readonly HttpClient _httpClient = new();
        private readonly List<DropData> _dropBuffer = [];
        private DateTime _lastUploadTime = DateTime.UtcNow;
        private readonly Lock _bufferLock = new();

        private string _lastKilledMob = "Unknown";
        private int _lastKilledMobId = 0;
        private DateTime _lastKillTime = DateTime.MinValue;

        private readonly HashSet<ulong> _deadMobs = [];
        private readonly Dictionary<uint, int> _inventorySnapshot = [];
        private DateTime _lastInventoryScan = DateTime.MinValue;

        private readonly HashSet<uint> _seenRollIds = [];

        public DropTracker(Config config, IClientState clientState, IPluginLog pluginLog, IDataManager data, IObjectTable objectTable, IFramework framework, IGameGui gameGui, IPartyList partyList)
        {
            _config = config;
            _clientState = clientState;
            _pluginLog = pluginLog;
            _data = data;
            _objectTable = objectTable;
            _framework = framework;
            _gameGui = gameGui;
            _partyList = partyList;

            _framework.Update += OnUpdate;
            _clientState.TerritoryChanged += OnTerritoryChanged;

            InitializeInventorySnapshot();
        }

        public void Dispose()
        {
            _framework.Update -= OnUpdate;
            _clientState.TerritoryChanged -= OnTerritoryChanged;

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

        private void OnTerritoryChanged(ushort id)
        {
            _deadMobs.Clear();
            _seenRollIds.Clear();
        }

        private void OnUpdate(IFramework framework)
        {
            if (!_config.IsLoggingEnabled) return;
            if (_clientState.LocalPlayer == null) return;

            CheckForKills();

            if ((DateTime.UtcNow - _lastInventoryScan).TotalMilliseconds > 500)
            {
                CheckForLoot();
                CheckForLootRolls();
                _lastInventoryScan = DateTime.UtcNow;
            }
        }

        private void CheckForKills()
        {
            var player = _clientState.LocalPlayer;
            if (player == null) return;

            foreach (var actor in _objectTable)
            {
                if (actor.ObjectKind != ObjectKind.BattleNpc) continue;

                var battleNpc = (IBattleNpc)actor;

                if (battleNpc.IsDead || battleNpc.CurrentHp == 0)
                {
                    if (_deadMobs.Contains(actor.GameObjectId)) continue;

                    var dist = System.Numerics.Vector3.Distance(player.Position, actor.Position);
                    if (dist > 40.0f) continue;

                    bool isAggroed = false;
                    ulong targetId = battleNpc.TargetObjectId;

                    if (targetId == player.GameObjectId)
                    {
                        isAggroed = true;
                    }
                    else if (_partyList.Length > 0)
                    {
                        foreach (var member in _partyList)
                        {
                            if (member.GameObject != null && member.GameObject.GameObjectId == targetId)
                            {
                                isAggroed = true;
                                break;
                            }
                        }
                    }

                    if (!isAggroed && player.TargetObjectId == actor.GameObjectId)
                    {
                        isAggroed = true;
                    }

                    if (isAggroed)
                    {
                        HandleKill(battleNpc);
                    }

                    _deadMobs.Add(actor.GameObjectId);
                }
                else
                {
                    if (_deadMobs.Contains(actor.GameObjectId)) _deadMobs.Remove(actor.GameObjectId);
                }
            }
        }

        private unsafe void CheckForLootRolls()
        {
            var addonPtr = _gameGui.GetAddonByName("Loot", 1);

            if (addonPtr.Address == nint.Zero) return;

            var addon = (AtkUnitBase*)addonPtr.Address;

            if (addon == null || !addon->IsVisible) return;

            var count = addon->AtkValuesCount;
            var values = addon->AtkValues;

            for (int i = 0; i < count; i++)
            {
                var val = values[i];
                if (val.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int) continue;

                uint possibleItemId = (uint)val.Int;
                if (possibleItemId < 100 || possibleItemId > 40000) continue;

                if (_data.GetExcelSheet<LuminaItem>()?.HasRow(possibleItemId) == true)
                {
                    uint pseudoId = (uint)((i << 16) | possibleItemId);

                    if (!_seenRollIds.Contains(pseudoId))
                    {
                        HandleChestDrop(possibleItemId);
                        _seenRollIds.Add(pseudoId);
                    }
                }
            }
        }

        private unsafe void CheckForLoot()
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return;

            var currentCounts = new Dictionary<uint, int>();

            for (int i = 0; i < 4; i++)
            {
                var container = manager->GetInventoryContainer((InventoryType)((int)InventoryType.Inventory1 + i));
                if (container == null) continue;

                for (int slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId == 0) continue;

                    if (!currentCounts.ContainsKey(item->ItemId)) currentCounts[item->ItemId] = 0;
                    currentCounts[item->ItemId] += (int)item->Quantity;
                }
            }

            foreach (var kvp in currentCounts)
            {
                var itemId = kvp.Key;
                var count = kvp.Value;

                int oldCount = 0;
                if (_inventorySnapshot.ContainsKey(itemId)) oldCount = _inventorySnapshot[itemId];

                if (count > oldCount)
                {
                    int diff = count - oldCount;
                    HandleDrop(itemId, diff);
                }
            }

            _inventorySnapshot.Clear();
            foreach (var kvp in currentCounts) _inventorySnapshot[kvp.Key] = kvp.Value;
        }

        private unsafe void InitializeInventorySnapshot()
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return;

            _inventorySnapshot.Clear();
            for (int i = 0; i < 4; i++)
            {
                var container = manager->GetInventoryContainer((InventoryType)((int)InventoryType.Inventory1 + i));
                if (container == null) continue;

                for (int slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item != null && item->ItemId != 0)
                    {
                        if (!_inventorySnapshot.ContainsKey(item->ItemId)) _inventorySnapshot[item->ItemId] = 0;
                        _inventorySnapshot[item->ItemId] += (int)item->Quantity;
                    }
                }
            }
        }

        private void HandleKill(IBattleNpc actor)
        {
            try
            {
                var name = actor.Name.ToString();
                var mobId = (int)actor.NameId;

                _lastKilledMob = name;
                _lastKilledMobId = mobId;
                _lastKillTime = DateTime.UtcNow;

                LogAndBuffer($"MOB-{name}", mobId, 1, false, true, null, null, "Kill");
            }
            catch (Exception ex) { _pluginLog.Error(ex, "Error handling kill"); }
        }

        private void HandleDrop(uint itemId, int quantity)
        {
            ProcessLootItem(itemId, quantity, "Inventory");
        }

        private void HandleChestDrop(uint itemId)
        {
            ProcessLootItem(itemId, 1, "Chest Roll");
        }

        private void ProcessLootItem(uint itemId, int quantity, string source)
        {
            try
            {
                if ((itemId > 0 && itemId < 100) || itemId == 21072) return;

                var itemSheet = _data.GetExcelSheet<LuminaItem>();
                string cleanName = "Unknown Item";

                if (itemSheet != null && itemSheet.TryGetRow(itemId, out var itemRow))
                {
                    if (itemRow.ItemUICategory.Value.RowId == 63) return;
                    cleanName = itemRow.Name.ToString();
                }

                string sourceMob = "Unknown";
                int sourceMobId = 0;

                if ((DateTime.UtcNow - _lastKillTime).TotalSeconds < 30)
                {
                    sourceMob = _lastKilledMob;
                    sourceMobId = _lastKilledMobId;
                }

                LogAndBuffer(cleanName, (int)itemId, quantity, false, false, sourceMob, sourceMobId, source);
            }
            catch (Exception ex) { _pluginLog.Error(ex, "Error handling loot item"); }
        }

        private void LogAndBuffer(string name, int itemId, int quantity, bool isHq, bool isMob, string sourceMob, int? sourceMobId, string sourceMethod)
        {
            ushort zoneId = _clientState.TerritoryType;
            string userHash = _clientState.LocalContentId == 0 ? "000000" : _clientState.LocalContentId.ToString("X");

            if (_config.EnableDebugLogging)
            {
                _pluginLog.Debug($"[Tracking] [{sourceMethod}] {quantity}x {name} (ID: {itemId} | Zone: {zoneId})");
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
                SourceMobID = isMob ? null : sourceMobId
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
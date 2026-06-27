using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace GlamLevels.Services
{
    public class GlamourerIpc : IDisposable
    {
        // Glamourer fires state events under the "Penumbra" prefix.
        // StateChangeType.Design = 9 per Glamourer.Api.Enums.StateChangeType.
        private const string EventLabel = "Penumbra.StateChangedWithType";
        private const int StateChangeTypeDesign = 9;

        private readonly IDalamudPluginInterface _pi;
        private readonly IPluginLog _log;
        private readonly IChatGui _chat;
        private readonly Action<nint, int> _handler;

        public bool DebugMode { get; set; } = false;

        public event Action OnDesignApplied;

        public GlamourerIpc(IDalamudPluginInterface pi, IPluginLog log, IChatGui chat)
        {
            _pi = pi;
            _log = log;
            _chat = chat;
            _handler = OnStateChanged;
            Subscribe();
        }

        private void Subscribe()
        {
            try
            {
                _pi.GetIpcSubscriber<nint, int, object>(EventLabel).Subscribe(_handler);
                _log.Info("[GlamLevels] Subscribed to {Label}", EventLabel);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[GlamLevels] Could not subscribe to {Label}", EventLabel);
            }
        }

        private void OnStateChanged(nint objectPtr, int changeType)
        {
            if (DebugMode)
                _chat.Print($"[GlamLevels] StateChangedWithType fired: changeType={changeType}");

            if (changeType == StateChangeTypeDesign)
                OnDesignApplied?.Invoke();
        }

        public bool IsAvailable()
        {
            try
            {
                _pi.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersions").InvokeFunc();
                return true;
            }
            catch { return false; }
        }

        // Returns the GUID and display name of the design currently applied to the local player,
        // by comparing the live state equipment slots against Glamourer's design files on disk.
        public (Guid DesignGuid, string DesignName) GetCurrentDesignInfo()
        {
            try
            {
                var (ec, state) = _pi
                    .GetIpcSubscriber<int, uint, (int, JObject)>("Glamourer.GetState")
                    .InvokeFunc(0, 0u);

                if (ec != 0 || state == null)
                {
                    _log.Warning("[GlamLevels] GetState returned ec={Ec}, null state={Null}", ec, state == null);
                    return default;
                }

                var stateEquipment = state["Equipment"] as JObject;
                if (stateEquipment == null) return default;

                return MatchDesignFromDisk(stateEquipment);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[GlamLevels] GetCurrentDesignInfo failed");
                return default;
            }
        }

        // Scans Glamourer design files on disk and finds the one whose applied equipment
        // slots all match the current state. Returns the best (highest score) match.
        private (Guid, string) MatchDesignFromDisk(JObject stateEquipment)
        {
            var designsDir = Path.Combine(_pi.ConfigDirectory.Parent.FullName, "Glamourer", "designs");

            if (!Directory.Exists(designsDir))
            {
                _log.Warning("[GlamLevels] Glamourer designs dir not found: {Dir}", designsDir);
                return default;
            }

            Guid bestGuid = Guid.Empty;
            string bestName = null;
            int bestScore = 0;

            foreach (var file in Directory.GetFiles(designsDir, "*.json"))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var guid))
                    continue;

                try
                {
                    var design = JObject.Parse(File.ReadAllText(file));
                    var designEquip = design["Equipment"] as JObject;
                    if (designEquip == null) continue;

                    int score = 0;
                    bool mismatch = false;

                    foreach (var prop in designEquip.Properties())
                    {
                        var dSlot = prop.Value as JObject;
                        // Only check slots the design explicitly applies and that have an ItemId
                        if (dSlot?["Apply"]?.Value<bool>() != true) continue;
                        if (dSlot["ItemId"] == null) continue;

                        var sSlot = stateEquipment[prop.Name] as JObject;
                        if (sSlot == null || dSlot["ItemId"].Value<long>() != sSlot["ItemId"]?.Value<long>())
                        {
                            mismatch = true;
                            break;
                        }
                        score++;
                    }

                    if (!mismatch && score > bestScore)
                    {
                        bestScore = score;
                        bestGuid = guid;
                        bestName = design["Name"]?.Value<string>() ?? guid.ToString();
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "[GlamLevels] Could not parse design file {File}", file);
                }
            }

            if (bestScore > 0)
            {
                _log.Debug("[GlamLevels] Matched design '{Name}' ({Guid}) score={Score}", bestName, bestGuid, bestScore);
                return (bestGuid, bestName);
            }

            _log.Warning("[GlamLevels] No design matched current equipment state");
            return default;
        }

        public void DumpState(string filePath)
        {
            try
            {
                var (ec, state) = _pi
                    .GetIpcSubscriber<int, uint, (int, JObject)>("Glamourer.GetState")
                    .InvokeFunc(0, 0u);
                var content = $"ec={ec}\n" + (state?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "null");
                File.WriteAllText(filePath, content);
            }
            catch (Exception ex)
            {
                File.WriteAllText(filePath, $"Exception: {ex}");
            }
        }

        public void Dispose()
        {
            try
            {
                _pi.GetIpcSubscriber<nint, int, object>(EventLabel).Unsubscribe(_handler);
            }
            catch { }
        }
    }
}

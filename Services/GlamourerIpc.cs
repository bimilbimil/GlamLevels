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
                _chat.Print($"[GlamLevels] StateChanged: type={changeType} ptr={objectPtr} (Design={StateChangeTypeDesign})");

            if (changeType == StateChangeTypeDesign)
                OnDesignApplied?.Invoke();
        }

        // Returns name→guid map for all designs, for use in user-facing hints.
        public Dictionary<Guid, string> GetAllDesignNames()
        {
            try
            {
                var list = _pi
                    .GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended")
                    .InvokeFunc();
                if (list == null) return new();
                var result = new Dictionary<Guid, string>();
                foreach (var (guid, info) in list) result[guid] = info.Item1;
                return result;
            }
            catch { return new Dictionary<Guid, string>(); }
        }

        // Returns the set of all design GUIDs currently registered in Glamourer.
        // Empty set means Glamourer is unavailable — callers should not treat all snapshots as orphaned.
        public HashSet<Guid> GetAllDesignGuids()
        {
            try
            {
                var list = _pi
                    .GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended")
                    .InvokeFunc();
                return list != null ? new HashSet<Guid>(list.Keys) : new HashSet<Guid>();
            }
            catch { return new HashSet<Guid>(); }
        }

        // Looks up the display name for a design GUID via Glamourer's design list.
        public string LookupDesignName(Guid designGuid)
        {
            if (designGuid == Guid.Empty) return null;
            try
            {
                var list = _pi
                    .GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended")
                    .InvokeFunc();
                return list?.TryGetValue(designGuid, out var info) == true ? info.Item1 : null;
            }
            catch { return null; }
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

        // Returns the GUID, display name, and state hash for the current design.
        // StateHash is always populated when GetState succeeds — it is the primary fallback identifier
        // when disk-based GUID matching fails (e.g. designs with no Apply=true equipment slots).
        public (Guid DesignGuid, string DesignName, string StateHash) GetCurrentDesignInfo()
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

                var hash = ComputeStateHash(stateEquipment);

                // 1. Ask Glamourer directly which design is applied (works on newer versions)
                var ipcGuid = TryGetAppliedDesignGuid();
                if (ipcGuid != Guid.Empty)
                {
                    var ipcName = LookupDesignName(ipcGuid);
                    if (!string.IsNullOrEmpty(ipcName))
                        return (ipcGuid, ipcName, hash);
                }

                // 2. Match via GetDesignJObject IPC — uses same serialization as GetState,
                //    so ItemIds are consistent. More reliable than reading disk files.
                var (ipcMatchGuid, ipcMatchName) = MatchDesignViaIpc(stateEquipment);
                if (ipcMatchGuid != Guid.Empty)
                    return (ipcMatchGuid, ipcMatchName, hash);

                // 3. Last resort: disk-based matching (older Glamourer, no GetDesignJObject)
                var (diskGuid, diskName) = MatchDesignFromDisk(stateEquipment);
                return (diskGuid, diskName, hash);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[GlamLevels] GetCurrentDesignInfo failed");
                return default;
            }
        }

        // Tries several known Glamourer IPC endpoints to get the GUID of the design
        // currently applied to the local player. Returns Guid.Empty if none work.
        private Guid TryGetAppliedDesignGuid()
        {
            // int-based object index (0 = local player)
            var intEndpoints = new[] { "Glamourer.GetStateDesign", "Glamourer.GetCurrentDesign", "Glamourer.GetAppliedDesign" };
            foreach (var ep in intEndpoints)
            {
                try
                {
                    var guid = _pi.GetIpcSubscriber<int, Guid>(ep).InvokeFunc(0);
                    if (guid != Guid.Empty) { _log.Debug("[GlamLevels] Got design GUID from {Ep}", ep); return guid; }
                }
                catch { }
            }
            // nint-based object pointer variants
            var ptrEndpoints = new[] { "Glamourer.GetStateDesign", "Glamourer.GetCurrentDesign" };
            foreach (var ep in ptrEndpoints)
            {
                try
                {
                    var guid = _pi.GetIpcSubscriber<nint, Guid>(ep).InvokeFunc(0);
                    if (guid != Guid.Empty) { _log.Debug("[GlamLevels] Got design GUID from {Ep} (ptr)", ep); return guid; }
                }
                catch { }
            }
            return Guid.Empty;
        }

        // MD5 of sorted "Slot:ItemId" pairs — stable identifier for a given equipment loadout.
        private static string ComputeStateHash(JObject equipment)
        {
            var sorted = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var prop in equipment.Properties())
                sorted[prop.Name] = (prop.Value as JObject)?["ItemId"]?.Value<long>() ?? 0;
            var raw = string.Join(";", System.Linq.Enumerable.Select(sorted, kv => $"{kv.Key}:{kv.Value}"));
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }

        // Matches via GetDesignJObject IPC — both sides use Glamourer's current serialization,
        // so ItemIds are in the same format (unlike disk files which may lag behind).
        private (Guid, string) MatchDesignViaIpc(JObject stateEquipment)
        {
            try
            {
                var designs = _pi
                    .GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended")
                    .InvokeFunc();
                if (designs == null || designs.Count == 0) return default;

                var stateSlots = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in stateEquipment.Properties())
                    stateSlots[p.Name.Replace(" ", "")] = p.Value as JObject;

                Guid bestGuid = Guid.Empty;
                string bestName = null;
                int bestScore = 0;

                foreach (var (guid, info) in designs)
                {
                    JObject designJObj;
                    try
                    {
                        designJObj = _pi.GetIpcSubscriber<Guid, JObject>("Glamourer.GetDesignJObject").InvokeFunc(guid);
                    }
                    catch { continue; }

                    var designEquip = designJObj?["Equipment"] as JObject;
                    if (designEquip == null) continue;

                    int score = 0;
                    bool mismatch = false;

                    foreach (var prop in designEquip.Properties())
                    {
                        var dSlot = prop.Value as JObject;
                        if (dSlot?["Apply"]?.Value<bool>() != true) continue;
                        var dItemId = dSlot["ItemId"]?.Value<long>() ?? 0;
                        if (dItemId == 0) continue;

                        stateSlots.TryGetValue(prop.Name.Replace(" ", ""), out var sSlot);
                        var sItemId = sSlot?["ItemId"]?.Value<long>() ?? -1;
                        if (dItemId != sItemId) { mismatch = true; break; }
                        score++;
                    }

                    if (!mismatch && score > bestScore)
                    {
                        bestScore = score;
                        bestGuid = guid;
                        bestName = info.Item1;
                    }
                }

                if (bestScore > 0)
                {
                    _log.Debug("[GlamLevels] IPC-matched design '{Name}' ({Guid}) score={Score}", bestName, bestGuid, bestScore);
                    return (bestGuid, bestName);
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[GlamLevels] MatchDesignViaIpc failed");
            }
            return default;
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

            // Normalize slot names by removing spaces — different Glamourer versions serialize
            // slot names differently ("Main Hand" vs "MainHand"). Strip spaces on both sides.
            var stateSlots = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in stateEquipment.Properties())
                stateSlots[prop.Name.Replace(" ", "")] = prop.Value as JObject;

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
                        if (dSlot?["Apply"]?.Value<bool>() != true) continue;
                        if (dSlot["ItemId"] == null) continue;
                        var dItemId = dSlot["ItemId"].Value<long>();
                        if (dItemId == 0) continue; // 0 = slot not set in design, skip

                        stateSlots.TryGetValue(prop.Name.Replace(" ", ""), out var sSlot);
                        var sItemId = sSlot?["ItemId"]?.Value<long>() ?? -1;
                        // Allow ±1 tolerance: design files and GetState use different item ID
                        // representations across Glamourer versions (e.g. 40022 vs 40021)
                        if (Math.Abs(dItemId - sItemId) > 1)
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

        public void PrintMatchDiagnostics()
        {
            var designsDir = Path.Combine(_pi.ConfigDirectory.Parent.FullName, "Glamourer", "designs");
            if (!Directory.Exists(designsDir)) { _chat.Print($"[GlamLevels] Designs dir NOT found: {designsDir}"); return; }
            var files = Directory.GetFiles(designsDir, "*.json");
            _chat.Print($"[GlamLevels] Designs dir: {files.Length} files");

            try
            {
                var (ec, state) = _pi.GetIpcSubscriber<int, uint, (int, JObject)>("Glamourer.GetState").InvokeFunc(0, 0u);
                var stateEquipment = state?["Equipment"] as JObject;
                if (stateEquipment == null) { _chat.Print($"[GlamLevels] GetState ec={ec}, no Equipment block"); return; }

                // Show the state's equipment structure
                var stateKeys = string.Join(", ", System.Linq.Enumerable.Take(
                    System.Linq.Enumerable.Select(stateEquipment.Properties(), p => p.Name), 4));
                _chat.Print($"[GlamLevels] State equipment keys (first 4): {stateKeys}");

                var stateSlots = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (var sp in stateEquipment.Properties())
                    stateSlots[sp.Name.Replace(" ", "")] = sp.Value as JObject;

                // Find first design with an Apply=true slot and print the comparison
                foreach (var file in files)
                {
                    if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _)) continue;
                    var design = JObject.Parse(File.ReadAllText(file));
                    var designEquip = design["Equipment"] as JObject;
                    if (designEquip == null) continue;
                    foreach (var prop in designEquip.Properties())
                    {
                        var dSlot = prop.Value as JObject;
                        if (dSlot?["Apply"]?.Value<bool>() != true) continue;
                        if (dSlot["ItemId"] == null) continue;
                        var designItemId = dSlot["ItemId"].Value<long>();
                        stateSlots.TryGetValue(prop.Name.Replace(" ", ""), out var sSlot);
                        var stateItemId = sSlot?["ItemId"]?.Value<long>();
                        _chat.Print($"[GlamLevels] Slot [{prop.Name}]: design ItemId={designItemId} (zero={designItemId == 0}) | state ItemId={stateItemId?.ToString() ?? "(not found)"}");
                        _chat.Print($"[GlamLevels] Design slot keys: {string.Join(", ", System.Linq.Enumerable.Select(dSlot.Properties(), p => p.Name))}");
                        return;
                    }
                }
                _chat.Print("[GlamLevels] No Apply=true slots found in any design file");
            }
            catch (Exception ex)
            {
                _chat.Print($"[GlamLevels] Diagnostics threw: {ex.Message}");
            }
        }

        // Prints a slot-by-slot comparison of the oldest design file vs current GetState.
        public void PrintOldestDesignComparison() => PrintDesignComparison(oldest: true);

        // Prints a slot-by-slot comparison of the newest design file vs current GetState.
        public void PrintNewDesignComparison() => PrintDesignComparison(oldest: false);

        private void PrintDesignComparison(bool oldest)
        {
            var designsDir = Path.Combine(_pi.ConfigDirectory.Parent.FullName, "Glamourer", "designs");
            if (!Directory.Exists(designsDir)) { _chat.Print($"[GlamLevels] Designs dir not found: {designsDir}"); return; }

            var files = new System.IO.DirectoryInfo(designsDir).GetFiles("*.json");
            if (files.Length == 0) { _chat.Print("[GlamLevels] No design files found."); return; }

            System.Array.Sort(files, (a, b) =>
                oldest ? a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc)
                       : b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            var target = files[0];
            var label = oldest ? "Oldest" : "Newest";
            _chat.Print($"[GlamLevels] {label} design file: {target.Name} (modified {target.LastWriteTimeUtc:MM/dd HH:mm:ss} UTC)");

            JObject design;
            try { design = JObject.Parse(System.IO.File.ReadAllText(target.FullName)); }
            catch (Exception ex) { _chat.Print($"[GlamLevels] Could not parse: {ex.Message}"); return; }

            _chat.Print($"[GlamLevels] Design name: \"{design["Name"]?.Value<string>() ?? "(no name)"}\"");

            var designEquip = design["Equipment"] as JObject;
            if (designEquip == null) { _chat.Print("[GlamLevels] No Equipment block in design file."); return; }

            JObject stateEquipment;
            try
            {
                var (ec, state) = _pi.GetIpcSubscriber<int, uint, (int, JObject)>("Glamourer.GetState").InvokeFunc(0, 0u);
                stateEquipment = state?["Equipment"] as JObject;
                if (stateEquipment == null) { _chat.Print($"[GlamLevels] GetState ec={ec}, no Equipment."); return; }
            }
            catch (Exception ex) { _chat.Print($"[GlamLevels] GetState failed: {ex.Message}"); return; }

            var stateSlots = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in stateEquipment.Properties())
                stateSlots[sp.Name.Replace(" ", "")] = sp.Value as JObject;

            int printed = 0;
            foreach (var prop in designEquip.Properties())
            {
                var dSlot = prop.Value as JObject;
                var apply = dSlot?["Apply"]?.Value<bool>();
                var dItemId = dSlot?["ItemId"]?.Value<long>() ?? 0;
                stateSlots.TryGetValue(prop.Name.Replace(" ", ""), out var sSlot);
                var sItemId = sSlot?["ItemId"]?.Value<long>();

                var match = sItemId.HasValue && dItemId == sItemId.Value ? "MATCH" : "DIFF";
                _chat.Print($"[GlamLevels]  {prop.Name,-12} apply={apply?.ToString() ?? "?"} design={dItemId,6}  state={sItemId?.ToString() ?? "?",6}  {match}");
                if (++printed >= 12) { _chat.Print("[GlamLevels] (first 12 slots shown)"); break; }
            }
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
            try { _pi.GetIpcSubscriber<nint, int, object>(EventLabel).Unsubscribe(_handler); } catch { }
        }
    }
}

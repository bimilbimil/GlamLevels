using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GlamLevels.Services
{
    public class PenumbraIpc
    {
        private readonly IDalamudPluginInterface _pi;
        private readonly IPluginLog _log;

        public PenumbraIpc(IDalamudPluginInterface pi, IPluginLog log)
        {
            _pi = pi;
            _log = log;
        }

        public bool IsAvailable()
        {
            try
            {
                _pi.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersions").InvokeFunc();
                return true;
            }
            catch { return false; }
        }

        public List<(string Dir, string Name)> GetMods()
        {
            try
            {
                var raw = _pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc();
                var result = new List<(string, string)>();
                foreach (var kv in raw) result.Add((kv.Key, kv.Value));
                return result;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[GlamLevels] Penumbra.GetModList failed");
                return new List<(string, string)>();
            }
        }

        // Returns the Guid+Name of the collection Penumbra uses for the local player (object index 0).
        public (Guid Id, string Name) GetPlayerCollectionInfo()
        {
            try
            {
                var result = _pi
                    .GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5")
                    .InvokeFunc(0);
                var (valid, _, collection) = result;
                return valid ? collection : GetDefaultCollectionInfo();
            }
            catch { return GetDefaultCollectionInfo(); }
        }

        public string GetPlayerCollection() => GetPlayerCollectionInfo().Name;

        public string GetDefaultCollection()
        {
            try { return _pi.GetIpcSubscriber<string>("Penumbra.GetDefaultCollectionName").InvokeFunc(); }
            catch { return "Default"; }
        }

        private (Guid Id, string Name) GetDefaultCollectionInfo()
        {
            try
            {
                var name = _pi.GetIpcSubscriber<string>("Penumbra.GetDefaultCollectionName").InvokeFunc();
                var matches = _pi.GetIpcSubscriber<string, List<(Guid, string)>>("Penumbra.GetCollectionsByIdentifier").InvokeFunc(name);
                return matches?.Count > 0 ? matches[0] : (Guid.Empty, name);
            }
            catch { return (Guid.Empty, "Default"); }
        }

        // Resolves a collection name to its Guid for V5 endpoints.
        public Guid ResolveCollectionGuid(string name)
        {
            try
            {
                var matches = _pi.GetIpcSubscriber<string, List<(Guid, string)>>("Penumbra.GetCollectionsByIdentifier").InvokeFunc(name);
                return matches?.Count > 0 ? matches[0].Item1 : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        // V5: takes Guid, returns (int ec, (enabled, priority, settings, inherited)?)
        // ec 0=Success, 1=NothingChanged; priority is result.Item2.Value.Item2
        public int GetModPriority(Guid collectionId, string modDir)
        {
            try
            {
                var result = _pi
                    .GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>
                    ("Penumbra.GetCurrentModSettings.V5")
                    .InvokeFunc(collectionId, modDir, "", false);

                if (result.Item1 == 0 && result.Item2.HasValue)
                    return result.Item2.Value.Item2;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[GlamLevels] GetModPriority failed for {Dir}", modDir);
            }
            return 0;
        }

        // V5: takes Guid, returns int (0=Success, 1=NothingChanged, other=error).
        public int SetModPriority(Guid collectionId, string modDir, int priority)
        {
            try
            {
                return _pi
                    .GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5")
                    .InvokeFunc(collectionId, modDir, "", priority);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[GlamLevels] SetModPriority exception for {Dir}", modDir);
                return -1;
            }
        }
    }
}

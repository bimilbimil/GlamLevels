using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using GlamLevels.Core;

namespace GlamLevels.Services
{
    public class SnapshotService
    {
        private readonly Configuration _config;
        private readonly PenumbraIpc _penumbra;
        private readonly IPluginLog _log;
        private readonly IChatGui _chat;

        // Priority assigned to mods that didn't exist when a snapshot was taken,
        // pushing them below everything so they can't conflict with the design.
        private const int NewModPriority = -999;

        public SnapshotService(Configuration config, PenumbraIpc penumbra, IPluginLog log, IChatGui chat)
        {
            _config = config;
            _penumbra = penumbra;
            _log = log;
            _chat = chat;
        }

        public const string LatestKey = "[latest]";

        public string FindKeyByStateHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            foreach (var (key, snap) in _config.Snapshots)
                if (snap.StateHash == hash) return key;
            return null;
        }

        public bool Save(string name, Guid collectionId, string collectionName, Guid designGuid = default, string stateHash = null, bool silent = false)
        {
            var mods = _penumbra.GetMods();
            if (mods.Count == 0)
            {
                if (!silent)
                    _chat.Print("[GlamLevels] Could not reach Penumbra — is it installed and enabled?");
                return false;
            }

            var entries = new List<ModPriorityEntry>();
            var knownMods = new List<int>();

            foreach (var (dir, _) in mods)
            {
                var idx = _config.GetOrAddModIndex(dir);
                knownMods.Add(idx);
                var priority = _penumbra.GetModPriority(collectionId, dir);
                if (priority != 0)
                    entries.Add(new ModPriorityEntry { Mod = idx, Priority = priority });
            }

            _config.Snapshots[name] = new DesignSnapshot
            {
                Collection = collectionName,
                CollectionGuid = collectionId == Guid.Empty ? null : collectionId.ToString(),
                DesignGuid = designGuid == Guid.Empty ? null : designGuid.ToString(),
                StateHash = stateHash,
                Priorities = entries,
                KnownMods = knownMods,
                CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            _config.Save();

            if (!silent)
                _chat.Print($"[GlamLevels] Saved \"{name}\": {entries.Count} non-default priorities, {knownMods.Count} mods tracked (collection: {collectionName}).");
            return true;
        }

        public string FindKeyByDesignGuid(Guid designGuid)
        {
            if (designGuid == Guid.Empty) return null;
            var guidStr = designGuid.ToString();
            foreach (var (key, snap) in _config.Snapshots)
                if (snap.DesignGuid == guidStr) return key;
            return null;
        }

        public bool Update(Guid designGuid, string stateHash, Guid collectionId, string collectionName)
        {
            var key = FindKeyByDesignGuid(designGuid) ?? FindKeyByStateHash(stateHash);
            if (key == null)
            {
                _chat.Print("[GlamLevels] No saved snapshot for the current design. Apply the design first to auto-save it.");
                return false;
            }
            return Save(key, collectionId, collectionName, designGuid, stateHash, silent: false);
        }

        public bool Restore(string name)
        {
            if (!_config.Snapshots.TryGetValue(name, out var snapshot))
            {
                _chat.Print($"[GlamLevels] No snapshot found for \"{name}\".");
                return false;
            }

            Guid collectionId;
            if (!string.IsNullOrEmpty(snapshot.CollectionGuid) && Guid.TryParse(snapshot.CollectionGuid, out var parsed))
                collectionId = parsed;
            else
                collectionId = _penumbra.ResolveCollectionGuid(snapshot.Collection);

            if (collectionId == Guid.Empty)
            {
                _chat.Print($"[GlamLevels] Cannot find collection \"{snapshot.Collection}\" — was it renamed or deleted?");
                return false;
            }

            _chat.Print($"[GlamLevels] Restoring \"{name}\" using collection [{snapshot.Collection}]...");

            // Indices of mods that have an explicitly saved priority
            var savedIndices = new HashSet<int>();
            foreach (var e in snapshot.Priorities) savedIndices.Add(e.Mod);

            // Indices of every mod that existed when the snapshot was taken
            var knownAtSaveTime = new HashSet<int>(snapshot.KnownMods);
            bool hasKnownMods = knownAtSaveTime.Count > 0;

            int restored = 0, reset = 0, excluded = 0, failed = 0;
            int firstFailEc = 0;
            string firstFailDir = null;

            void Apply(string modDir, int priority)
            {
                var ec = _penumbra.SetModPriority(collectionId, modDir, priority);
                if (ec == 0 || ec == 1)
                {
                    if (priority == NewModPriority) excluded++;
                    else if (priority != 0) restored++;
                    else reset++;
                }
                else
                {
                    if (firstFailDir == null) { firstFailDir = modDir; firstFailEc = ec; }
                    failed++;
                }
            }

            // 1. Set saved mods to their recorded non-zero priority
            foreach (var entry in snapshot.Priorities)
                Apply(_config.ModIndex[entry.Mod], entry.Priority);

            // 2. For every other mod in the collection:
            //    - Known at save time → reset to 0 if it has drifted
            //    - Unknown (installed after snapshot) → push to -999
            var allMods = _penumbra.GetMods();
            foreach (var (dir, _) in allMods)
            {
                var idx = _config.GetOrAddModIndex(dir);
                if (savedIndices.Contains(idx)) continue;

                if (hasKnownMods && !knownAtSaveTime.Contains(idx))
                    Apply(dir, NewModPriority);
                else if (_penumbra.GetModPriority(collectionId, dir) != 0)
                    Apply(dir, 0);
            }

            var msg = $"[GlamLevels] Restored \"{name}\": {restored} set, {reset} reset to 0";
            if (excluded > 0) msg += $", {excluded} new mods pushed to {NewModPriority}";
            if (failed > 0) msg += $", {failed} failed (first: ec={firstFailEc} [{firstFailDir}])";
            _chat.Print(msg + ".");
            return failed == 0;
        }

        public IReadOnlyDictionary<string, DesignSnapshot> GetAll() => _config.Snapshots;

        public bool Delete(string name)
        {
            if (!_config.Snapshots.Remove(name)) return false;
            _config.Save();
            return true;
        }

        public bool Rename(string oldName, string newName)
        {
            if (!_config.Snapshots.TryGetValue(oldName, out var snapshot)) return false;
            _config.Snapshots.Remove(oldName);
            _config.Snapshots[newName] = snapshot;
            _config.Save();
            return true;
        }
    }
}

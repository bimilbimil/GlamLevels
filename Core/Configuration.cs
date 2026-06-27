using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace GlamLevels.Core
{
    [Serializable]
    public class ModPriorityEntry
    {
        public int Mod { get; set; }       // index into Configuration.ModIndex
        public int Priority { get; set; }
    }

    [Serializable]
    public class DesignSnapshot
    {
        public string Collection { get; set; } = "Default";
        public string CollectionGuid { get; set; } = null;
        public string DesignGuid { get; set; } = null;
        // Non-zero priority mods only — all others were 0 at save time
        public List<ModPriorityEntry> Priorities { get; set; } = new();
        // Indices (into ModIndex) of every mod that existed when this snapshot was taken.
        // Mods whose index is NOT here were installed after the snapshot — set to -999 on restore.
        public List<int> KnownMods { get; set; } = new();
        public long CapturedAt { get; set; }
    }

    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 2;

        // Global append-only list of mod directories. Snapshots reference mods by index.
        // Never reorder or remove entries — indices must stay stable.
        public List<string> ModIndex { get; set; } = new();

        public Dictionary<string, DesignSnapshot> Snapshots { get; set; } = new();

        [NonSerialized]
        private Dictionary<string, int> _modLookup;

        [NonSerialized]
        public IDalamudPluginInterface PluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            PluginInterface = pluginInterface;

            if (Version < 2)
            {
                // Old format stored full strings; incompatible — start clean
                Snapshots.Clear();
                ModIndex.Clear();
                Version = 2;
            }

            _modLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ModIndex.Count; i++)
                _modLookup[ModIndex[i]] = i;
        }

        // Returns the index for a mod directory, adding it to the global index if new.
        public int GetOrAddModIndex(string modDir)
        {
            if (_modLookup.TryGetValue(modDir, out var idx)) return idx;
            idx = ModIndex.Count;
            ModIndex.Add(modDir);
            _modLookup[modDir] = idx;
            return idx;
        }

        public void Save()
        {
            PluginInterface?.SavePluginConfig(this);
        }
    }
}

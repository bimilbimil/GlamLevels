using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GlamLevels.Core;
using GlamLevels.Services;
using ImGui = Dalamud.Bindings.ImGui.ImGui;

namespace GlamLevels.UI
{
    public class MainWindow : Window
    {
        private readonly Configuration _config;
        private readonly SnapshotService _snapshots;
        private readonly PenumbraIpc _penumbra;
        private readonly GlamourerIpc _glamourer;

        // Cached design list: GUID → name, refreshed every 30 seconds
        private Dictionary<Guid, string> _validDesigns = new();
        private DateTimeOffset _validDesignsRefreshedAt = DateTimeOffset.MinValue;

        // Pending rename from the identify dropdown (deferred so we don't modify collection mid-loop)
        private (string OldName, string NewName)? _pendingRename;

        public MainWindow(Configuration config, SnapshotService snapshots, PenumbraIpc penumbra, GlamourerIpc glamourer)
            : base("Glam Levels")
        {
            _config = config;
            _snapshots = snapshots;
            _penumbra = penumbra;
            _glamourer = glamourer;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(520, 260),
                MaximumSize = new Vector2(900, 600),
            };
        }

        public override void Draw()
        {
            if (ImGui.CollapsingHeader("How to use"))
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("1. Apply a Glamourer design for the first time.");
                ImGui.TextUnformatted("   Glam Levels will automatically save its mod priorities.");
                ImGui.Spacing();
                ImGui.TextUnformatted("2. If priorities get messed up later, apply the design");
                ImGui.TextUnformatted("   again then run:  /glamlevel fix");
                ImGui.TextUnformatted("   Or click Fix next to the design below.");
                ImGui.Spacing();
                ImGui.TextUnformatted("3. After adjusting priorities intentionally, run:");
                ImGui.TextUnformatted("   /glamlevel update  — or click Update below.");
                ImGui.Spacing();
                ImGui.TextUnformatted("Note: After fixing, use Redraw Self in Penumbra");
                ImGui.TextUnformatted("or re-enter GPose to see the changes take effect.");
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }

            // Refresh the live Glamourer design list at most once every 30 seconds
            if (DateTimeOffset.UtcNow - _validDesignsRefreshedAt > TimeSpan.FromSeconds(30))
            {
                var fetched = _glamourer.GetAllDesignNames();
                if (fetched.Count > 0)
                    _validDesigns = fetched;
                _validDesignsRefreshedAt = DateTimeOffset.UtcNow;
            }

            var all = _snapshots.GetAll();
            if (all.Count == 0)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("No designs saved yet.");
                ImGui.TextUnformatted("Apply a Glamourer design to automatically save its priorities.");
                return;
            }

            string toDelete = null;
            foreach (var (name, snapshot) in all)
            {
                var captured = DateTimeOffset.FromUnixTimeSeconds(snapshot.CapturedAt).LocalDateTime;

                // Flag snapshots whose Glamourer design has been deleted
                Guid snapGuid = Guid.Empty;
                var hasGuid = !string.IsNullOrEmpty(snapshot.DesignGuid) && Guid.TryParse(snapshot.DesignGuid, out snapGuid);
                var isOrphan = hasGuid && _validDesigns.Count > 0 && !_validDesigns.ContainsKey(snapGuid);
                // Show identify dropdown when the design couldn't be auto-named (no GUID matched)
                var needsIdentify = !hasGuid && !string.IsNullOrEmpty(snapshot.StateHash);

                if (isOrphan)
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.45f, 0.45f, 1f));
                ImGui.TextUnformatted(name);
                if (isOrphan)
                    ImGui.PopStyleColor();

                if (isOrphan && ImGui.IsItemHovered())
                    ImGui.SetTooltip("This design no longer exists in Glamourer. Click X to remove it.");

                ImGui.SameLine(200);
                ImGui.TextUnformatted(captured.ToString("MM/dd HH:mm"));
                ImGui.SameLine(290);

                if (ImGui.Button($"Fix##{name}"))
                    _snapshots.Restore(name);
                ImGui.SameLine();

                if (ImGui.Button($"Update##{name}"))
                {
                    Guid collId = Guid.Empty;
                    if (!string.IsNullOrEmpty(snapshot.CollectionGuid))
                        Guid.TryParse(snapshot.CollectionGuid, out collId);
                    if (collId == Guid.Empty)
                        collId = _penumbra.ResolveCollectionGuid(snapshot.Collection);

                    var (curGuid, _, curHash) = _glamourer.GetCurrentDesignInfo();
                    _snapshots.Save(name, collId, snapshot.Collection, curGuid, curHash);
                }
                ImGui.SameLine();

                if (needsIdentify && _validDesigns.Count > 0)
                {
                    ImGui.SetNextItemWidth(140);
                    if (ImGui.BeginCombo($"##{name}_identify", "Identify..."))
                    {
                        foreach (var (_, designName) in _validDesigns)
                        {
                            if (ImGui.Selectable(designName))
                                _pendingRename = (name, designName);
                        }
                        ImGui.EndCombo();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Pick the Glamourer design this snapshot belongs to.");
                    ImGui.SameLine();
                }

                if (ImGui.Button($"X##{name}"))
                    toDelete = name;
            }

            if (toDelete != null)
                _snapshots.Delete(toDelete);

            if (_pendingRename.HasValue)
            {
                _snapshots.Rename(_pendingRename.Value.OldName, _pendingRename.Value.NewName);
                _pendingRename = null;
            }
        }

        public void Dispose() { }
    }
}

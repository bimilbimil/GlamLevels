using System;
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

        public MainWindow(Configuration config, SnapshotService snapshots, PenumbraIpc penumbra)
            : base("Glam Levels")
        {
            _config = config;
            _snapshots = snapshots;
            _penumbra = penumbra;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(460, 260),
                MaximumSize = new Vector2(800, 600),
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

                ImGui.TextUnformatted(name);
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

                    Guid.TryParse(snapshot.DesignGuid ?? "", out var designGuid);
                    _snapshots.Save(name, collId, snapshot.Collection, designGuid);
                }
                ImGui.SameLine();

                if (ImGui.Button($"X##{name}"))
                    toDelete = name;
            }

            if (toDelete != null)
                _snapshots.Delete(toDelete);
        }

        public void Dispose() { }
    }
}

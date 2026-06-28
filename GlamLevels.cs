using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GlamLevels.Core;
using GlamLevels.Services;
using GlamLevels.UI;

namespace GlamLevels
{
    public sealed class GlamLevelsPlugin : IDalamudPlugin
    {
        public string Name => "Glam Levels";
        private const string Command = "/glamlevel";

        private readonly IDalamudPluginInterface _pi;
        private readonly ICommandManager _commands;
        private readonly IChatGui _chat;
        private readonly IPluginLog _log;
        private readonly IFramework _framework;

        public Configuration Configuration { get; }

        private readonly PenumbraIpc _penumbra;
        private readonly GlamourerIpc _glamourer;
        private readonly SnapshotService _snapshots;
        private readonly MainWindow _mainWindow;
        private readonly WindowSystem _windowSystem = new("GlamLevels");

        private bool _pendingSnapshot = false;

        public GlamLevelsPlugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IPluginLog pluginLog,
            IChatGui chatGui,
            IFramework framework)
        {
            _pi = pluginInterface;
            _commands = commandManager;
            _log = pluginLog;
            _chat = chatGui;
            _framework = framework;

            Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Configuration.Initialize(pluginInterface);

            _penumbra = new PenumbraIpc(pluginInterface, pluginLog);
            _glamourer = new GlamourerIpc(pluginInterface, pluginLog, chatGui);
            _snapshots = new SnapshotService(Configuration, _penumbra, pluginLog, chatGui);

            _glamourer.OnDesignApplied += OnDesignApplied;
            _framework.Update += OnFrameworkUpdate;

            _mainWindow = new MainWindow(Configuration, _snapshots, _penumbra, _glamourer);
            _windowSystem.AddWindow(_mainWindow);

            _commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open GlamLevels  |  save <name>  |  fix [name]  |  update  |  list  |  delete <name>"
            });

            pluginInterface.UiBuilder.Draw += DrawUi;
            pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            pluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
        }

        public void Dispose()
        {
            _glamourer.OnDesignApplied -= OnDesignApplied;
            _glamourer.Dispose();
            _framework.Update -= OnFrameworkUpdate;

            _pi.UiBuilder.Draw -= DrawUi;
            _pi.UiBuilder.OpenMainUi -= OpenMainUi;
            _pi.UiBuilder.OpenConfigUi -= OpenMainUi;

            _commands.RemoveHandler(Command);
            _windowSystem.RemoveAllWindows();
            _mainWindow.Dispose();
        }

        private void DrawUi() => _windowSystem.Draw();
        private void OpenMainUi() => _mainWindow.IsOpen = true;

        private void OnDesignApplied() => _pendingSnapshot = true;

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (!_pendingSnapshot) return;
            _pendingSnapshot = false;

            try
            {
                var (designGuid, designName, stateHash) = _glamourer.GetCurrentDesignInfo();
                var (collectionId, collectionName) = _penumbra.GetPlayerCollectionInfo();

                var existingKey = (designGuid != Guid.Empty ? _snapshots.FindKeyByDesignGuid(designGuid) : null)
                               ?? _snapshots.FindKeyByStateHash(stateHash);

                if (existingKey != null)
                {
                    _log.Debug("[GlamLevels] Design already tracked as '{Key}', skipping auto-save", existingKey);
                    return;
                }

                if (!string.IsNullOrEmpty(stateHash))
                {
                    if (!string.IsNullOrEmpty(designName))
                    {
                        _snapshots.Save(designName, collectionId, collectionName, designGuid, stateHash, silent: false);
                    }
                    else
                    {
                        // Disk matching failed — save with a date name and hint the user to rename
                        var saveName = $"Design {DateTime.Now:MM/dd HH:mm}";
                        _snapshots.Save(saveName, collectionId, collectionName, designGuid, stateHash, silent: false);
                        var designList = _glamourer.GetAllDesignNames();
                        if (designList.Count > 0)
                        {
                            var names = string.Join(", ", designList.Values);
                            _chat.Print($"[GlamLevels] Could not detect design name. Your designs: {names}");
                            _chat.Print($"[GlamLevels] Run: /glamlevel rename \"{saveName}\" \"<correct name>\"");
                        }
                    }
                }
                else
                {
                    _log.Debug("[GlamLevels] Could not read state, saving as [latest]");
                    _snapshots.Save(SnapshotService.LatestKey, collectionId, collectionName, silent: true);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[GlamLevels] Failed to auto-snapshot on design apply");
            }
        }

        private void OnCommand(string command, string args)
        {
            var parts = args.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                _mainWindow.IsOpen = true;
                return;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "save":
                    if (parts.Length < 2) { _chat.Print("[GlamLevels] Usage: /glamlevel save <name> [collection]"); return; }
                    if (parts.Length > 2)
                    {
                        var manualName = parts[2];
                        var manualGuid = _penumbra.ResolveCollectionGuid(manualName);
                        _snapshots.Save(parts[1], manualGuid, manualName);
                    }
                    else
                    {
                        var (cid, cname) = _penumbra.GetPlayerCollectionInfo();
                        _snapshots.Save(parts[1], cid, cname);
                    }
                    break;

                case "fix":
                    if (parts.Length >= 2)
                    {
                        _snapshots.Restore(parts[1]);
                    }
                    else
                    {
                        var (fixGuid, fixName, fixHash) = _glamourer.GetCurrentDesignInfo();
                        var fixKey = (fixGuid != Guid.Empty ? _snapshots.FindKeyByDesignGuid(fixGuid) : null)
                                  ?? _snapshots.FindKeyByStateHash(fixHash);
                        if (fixKey != null)
                            _snapshots.Restore(fixKey);
                        else if (!string.IsNullOrEmpty(fixHash))
                            _chat.Print("[GlamLevels] No snapshot for the current design. Apply it first to auto-save priorities.");
                        else
                            _snapshots.Restore(SnapshotService.LatestKey);
                    }
                    break;

                case "update":
                    var (upGuid, upName, upHash) = _glamourer.GetCurrentDesignInfo();
                    if (string.IsNullOrEmpty(upHash))
                    {
                        _chat.Print("[GlamLevels] Cannot read current state. Apply a design first, then run /glamlevel update.");
                        return;
                    }
                    var (upCid, upCname) = _penumbra.GetPlayerCollectionInfo();
                    _snapshots.Update(upGuid, upHash, upCid, upCname);
                    break;

                case "list":
                    var all = _snapshots.GetAll();
                    if (all.Count == 0) { _chat.Print("[GlamLevels] No snapshots saved."); return; }
                    _chat.Print("[GlamLevels] Saved snapshots:");
                    foreach (var (n, s) in all)
                        _chat.Print($"  {n} — {s.Priorities.Count} mods [{s.Collection}]");
                    break;

                case "delete":
                    if (parts.Length < 2) { _chat.Print("[GlamLevels] Usage: /glamlevel delete <name>"); return; }
                    if (_snapshots.Delete(parts[1]))
                        _chat.Print($"[GlamLevels] Deleted snapshot \"{parts[1]}\".");
                    else
                        _chat.Print($"[GlamLevels] No snapshot named \"{parts[1]}\".");
                    break;

                case "rename":
                    if (parts.Length < 3) { _chat.Print("[GlamLevels] Usage: /glamlevel rename \"<old name>\" \"<new name>\""); return; }
                    if (_snapshots.Rename(parts[1], parts[2]))
                        _chat.Print($"[GlamLevels] Renamed \"{parts[1]}\" → \"{parts[2]}\".");
                    else
                        _chat.Print($"[GlamLevels] No snapshot named \"{parts[1]}\".");
                    break;

                case "debug":
                    _glamourer.DebugMode = !_glamourer.DebugMode;
                    _chat.Print($"[GlamLevels] Debug mode {(_glamourer.DebugMode ? "ON" : "OFF")}");
                    _chat.Print($"  Glamourer: {_glamourer.IsAvailable()}, Penumbra: {_penumbra.IsAvailable()}");
                    var (dbgGuid, dbgName, dbgHash) = _glamourer.GetCurrentDesignInfo();
                    _chat.Print($"[GlamLevels]   Disk match: \"{dbgName ?? "none"}\" ({dbgGuid})");
                    _chat.Print($"[GlamLevels]   State hash: {dbgHash ?? "null"}");
                    _glamourer.PrintMatchDiagnostics();
                    var (dbgCid, dbgCname) = _penumbra.GetPlayerCollectionInfo();
                    _chat.Print($"  Player collection: [{dbgCname}] ({dbgCid})");
                    break;

                case "statedump":
                    var dumpPath = System.IO.Path.Combine(_pi.ConfigDirectory.FullName, "state_debug.json");
                    _glamourer.DumpState(dumpPath);
                    _chat.Print($"[GlamLevels] State written to: {dumpPath}");
                    break;

                default:
                    _chat.Print("[GlamLevels] Commands: save <name> [collection] | fix [name] | update | list | rename <old> <new> | delete <name> | debug");
                    break;
            }
        }
    }
}

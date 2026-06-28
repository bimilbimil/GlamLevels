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
                if (_glamourer.DebugMode)
                    _chat.Print("[GlamLevels] OnFrameworkUpdate: processing pending snapshot...");

                var (designGuid, designName, stateHash) = _glamourer.GetCurrentDesignInfo();
                var (collectionId, collectionName) = _penumbra.GetPlayerCollectionInfo();

                if (_glamourer.DebugMode)
                    _chat.Print($"[GlamLevels]   designGuid={designGuid}  name=\"{designName ?? "(null)"}\"  hash={stateHash ?? "null"}  collection={collectionName}");

                var existingKey = (designGuid != Guid.Empty ? _snapshots.FindKeyByDesignGuid(designGuid) : null)
                               ?? _snapshots.FindKeyByStateHash(stateHash);

                if (existingKey != null)
                {
                    if (_glamourer.DebugMode)
                        _chat.Print($"[GlamLevels]   Already tracked as \"{existingKey}\", skipping auto-save.");
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
                        var saveName = $"Design {DateTime.Now:MM/dd HH:mm}";
                        _snapshots.Save(saveName, collectionId, collectionName, designGuid, stateHash, silent: false);
                        _chat.Print($"[GlamLevels] Could not auto-detect design name — saved as \"{saveName}\".");
                        _chat.Print($"[GlamLevels] While this design is still applied: open /glamlevel and use Identify..., or run /glamlevel identify <name>.");
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

                case "identify":
                    var idName = AfterSubcommand(args, "identify");
                    if (string.IsNullOrEmpty(idName)) { _chat.Print("[GlamLevels] Usage: /glamlevel identify <design name>"); return; }
                    var (_, _, idHash) = _glamourer.GetCurrentDesignInfo();
                    if (string.IsNullOrEmpty(idHash)) { _chat.Print("[GlamLevels] Cannot read current state — is Glamourer available?"); return; }
                    var idKey = _snapshots.FindKeyByStateHash(idHash);
                    if (idKey == null) { _chat.Print("[GlamLevels] No saved snapshot for the current design. Apply it first."); return; }
                    if (_snapshots.Rename(idKey, idName))
                        _chat.Print($"[GlamLevels] Identified \"{idKey}\" → \"{idName}\".");
                    else
                        _chat.Print($"[GlamLevels] A snapshot named \"{idName}\" already exists.");
                    break;

                case "rename":
                    // Expects: rename "old name" "new name" — quotes required when names contain spaces
                    var renameParts = ParseQuotedPair(AfterSubcommand(args, "rename"));
                    if (renameParts == null) { _chat.Print("[GlamLevels] Usage: /glamlevel rename \"<old name>\" \"<new name>\""); return; }
                    if (_snapshots.Rename(renameParts.Value.Item1, renameParts.Value.Item2))
                        _chat.Print($"[GlamLevels] Renamed \"{renameParts.Value.Item1}\" → \"{renameParts.Value.Item2}\".");
                    else
                        _chat.Print($"[GlamLevels] No snapshot named \"{renameParts.Value.Item1}\".");
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

                case "testdesign":
                    _glamourer.PrintNewDesignComparison();
                    break;

                case "testolddesign":
                    _glamourer.PrintOldestDesignComparison();
                    break;

                case "statedump":
                    var dumpPath = System.IO.Path.Combine(_pi.ConfigDirectory.FullName, "state_debug.json");
                    _glamourer.DumpState(dumpPath);
                    _chat.Print($"[GlamLevels] State written to: {dumpPath}");
                    break;

                default:
                    _chat.Print("[GlamLevels] Commands: fix [name] | update | identify <name> | list | rename \"<old>\" \"<new>\" | delete <name> | save <name> [collection]");
                    break;
            }
        }

        // Returns everything after the subcommand word, with optional surrounding quotes stripped.
        private static string AfterSubcommand(string args, string sub)
        {
            var trimmed = args.Trim();
            if (trimmed.Length <= sub.Length) return "";
            return trimmed.Substring(sub.Length).Trim().Trim('"').Trim();
        }

        // Parses "\"old name\" \"new name\"" or "old new" into two tokens.
        private static (string, string)? ParseQuotedPair(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith('"'))
            {
                var close = s.IndexOf('"', 1);
                if (close < 0) return null;
                var first = s.Substring(1, close - 1);
                var rest = s.Substring(close + 1).Trim().Trim('"').Trim();
                return string.IsNullOrEmpty(rest) ? null : (first, rest);
            }
            // No quotes — split on first space
            var sp = s.IndexOf(' ');
            if (sp < 0) return null;
            return (s.Substring(0, sp), s.Substring(sp + 1).Trim());
        }
    }
}

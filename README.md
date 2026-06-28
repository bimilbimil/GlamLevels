# Glam Levels

![Glam Levels icon](glamlevels.png)

Automatically saves your Penumbra mod priorities when you apply a Glamourer design, and restores them instantly when priorities drift.

## The problem it solves

You have multiple Glamourer designs, each depending on specific mods winning priority conflicts in Penumbra. Switching between designs (or installing new mods) reshuffles those priorities, breaking your looks. Fixing them manually every time is tedious.

**Glam Levels handles this automatically.**

## Requirements

- [Glamourer](https://github.com/Ottermandias/Glamourer)
- [Penumbra](https://github.com/xivdev/Penumbra)

## Installation

Add the following URL to Dalamud's custom plugin repositories:

```
https://raw.githubusercontent.com/bimilbimil/GlamLevels/main/repo.json
```

> The repo will be live after the first release is published.

## How it works

**First apply** — When you apply a Glamourer design for the first time, Glam Levels automatically saves a snapshot of your current Penumbra mod priorities. If the design name can be detected, the snapshot is saved under that name automatically. Otherwise it's saved with a date name (see *Identifying old designs* below).

**Priorities drift** — After switching designs, installing new mods, or reshuffling priorities for another look, your saved design may no longer render correctly.

**Fix it** — Apply the Glamourer design again, then run `/glamlevel fix`. Glam Levels restores every mod to its correct priority for that design. New mods that didn't exist when the snapshot was taken are pushed to priority -999 so they can't interfere.

**After fixing** — Use **Redraw Self** in Penumbra (or re-enter GPose) to see the changes take effect.

**Changed your mind?** — If you've intentionally adjusted priorities and want to update the saved snapshot, run `/glamlevel update`.

## Identifying old designs

Glam Levels detects design names by comparing equipment item IDs. **Designs created with an older version of Glamourer, or designs migrated from another machine, use a different item ID format** that can't be matched automatically.

When this happens, the snapshot is saved with a date name like `Design 06/28 15:30`, and you'll see a chat message saying so.

**To name it properly**, while that design is still applied, either:

- Open `/glamlevel` and click the **Identify...** dropdown next to the date-named entry, then pick the correct design name from the list.
- Or run: `/glamlevel identify <design name>`

Once identified, Glam Levels tracks the design reliably by its equipment fingerprint — you won't need to identify it again.

> **Tip:** After first installing Glam Levels, apply each of your older designs once and identify them immediately. Future applies, fixes, and updates will all work automatically after that.

## Commands

| Command | Description |
|---|---|
| `/glamlevel` | Open the Glam Levels window |
| `/glamlevel fix` | Restore priorities for the currently active design |
| `/glamlevel fix <name>` | Restore priorities for a specific saved design |
| `/glamlevel update` | Update the current design's saved priorities to match Penumbra now |
| `/glamlevel identify <name>` | Name the currently applied (unidentified) design |
| `/glamlevel rename "<old>" "<new>"` | Rename a saved snapshot |
| `/glamlevel list` | List all saved design snapshots |
| `/glamlevel save <name>` | Manually save priorities under a custom name |
| `/glamlevel delete <name>` | Delete a saved snapshot |

## UI

Open the window with `/glamlevel`. Each saved design shows:
- **Fix** — restore priorities for that design
- **Update** — refresh the snapshot with current priorities
- **Identify...** — dropdown (shown for date-named snapshots only) to pick the correct Glamourer design name
- **X** — delete the snapshot

Snapshots shown in red have a design that no longer exists in Glamourer and can safely be deleted.

## Edge cases

- **New mods installed after a snapshot was taken** are automatically pushed to priority -999 on restore, so they can't conflict with the original design.
- **Mods that existed at snapshot time with priority 0** are reset back to 0 on restore, even if another design moved them higher.
- **Old or migrated designs** cannot be auto-named — apply them and use Identify... to name them once.

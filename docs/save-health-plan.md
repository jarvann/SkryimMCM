# Save health: in-game check and clean (plan, 1.4)

Goal: the AI can check the save the player is playing on, explain what's wrong in it, and with the player's agreement clean
it and put them back in the game on the cleaned save. Cleaning only takes effect by loading the cleaned file, so the
workflow always ends with a load.

Inspired by ReSaver (FallrimTools, by Mark Fairchild, Apache-2.0), which remains the reference: its source ships with the
installed tool, and `scripts/save-scan/` already drives its library headlessly.

## Tools

| Tool | What it does | Writes? |
|---|---|---|
| `CheckSave(saveName?)` | With no name: makes a fresh save of the current game (`SkyLink_Check_<time>`), waits until the file is complete, then scans it. With a name: scans that save. Reports the findings in plain language with counts and the scripts or mods involved. | One new save (the check save), nothing else |
| `CleanSave(saveName, what, dryRun=true)` | Removes the chosen problems from a **copy** (`<name>_cleaned`), never the original. `what`: unattached instances, undefined elements, undefined threads, or all safe ones. Dry run lists what would go. | A new save file |
| `CleanAndReload(what, confirm)` | The in-game flow: `CheckSave` (fresh save), `CleanSave` to a copy, load the copy, wait for the load, run `CheckSave` again on a new save to confirm the problems are gone. Needs the player's confirmation and the move safety rules (no menus, scenes, combat). | Two new saves and the cleaned copy |
| `ListSaves(filter?)` | The save folder of the running game (Steam or GOG), newest first, with character ID, name, level, location, time. | No |

## What the scan reports (ReSaver's checks)

- **Undefined scripts**: script data in the save with no script file installed (a mod removed or renamed scripts).
- **Unattached instances**: script instances bound to nothing (usually left by removed or updated mods; some are normal).
- **Undefined elements and threads**: active scripts, suspended stacks and messages that refer to undefined scripts.
- **Size and health**: broken or truncated Papyrus section, string table size, change form count, save size against the
  limits that cause problems.
- Grouped by plugin where the script can be traced to one, so the reply says "left over from <mod>" rather than raw names.

## Safety

- Never overwrites a save; cleaned output is always a new file, and the original is left untouched as the backup.
- Only works on complete files: waits for the save to finish writing (the game saves asynchronously, 10+ seconds with a
  heavy load order) before reading it.
- Loads only through the existing `LoadSave` and only when the move rules allow it (FEAT-259/260).
- Cleaning defaults to what ReSaver treats as safe (unattached instances, undefined elements). Terminating threads and
  removing script data of installed mods are opt-in, with a warning.
- Reads the save folder of the running game (BUG-088), and the character ID ties check results to the character (BUG-090).

## Build order

1. **Native scan (C#)**: ESS header and plugin list, LZ4, the Papyrus section (script, struct and reference
   definitions, instances, arrays, active scripts, messages), the unattached and undefined checks. Verified against the
   ReSaver scan (`scripts/save-scan`) on at least ten saves, Steam and GOG. About 3-5 hours of Claude time.
2. **`ListSaves` and `CheckSave`** with the fresh-save step and the wait for a complete file. About 1-2 hours.
3. **`CleanSave` through the installed ReSaver** (its `removeUnattachedInstances`, `removeUndefinedElements`, then
   `writeESS` to a new path), with a clear message when ReSaver isn't installed. About 1-2 hours.
4. **`CleanAndReload`** flow and its in-game test. About 1-2 hours plus test rounds.
5. **Native writer (later)**: replaces step 3 once it round-trips saves byte for byte (ReSaver's `verifyIdentical`),
   matches ReSaver's cleaned output, and cleaned saves load in game. About a day.

## Test plan

- Scan parity: native findings equal ReSaver's on the same saves.
- A save with known leftovers (uninstall a small scripted mod on a throwaway character) shows them, cleans them, and the
  reloaded game's check comes back clean.
- Original saves are untouched (hash before and after).
- Refusals: in a menu, a scene or combat, `CleanAndReload` refuses; without ReSaver, `CleanSave` says so.

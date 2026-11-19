# Save health: in-game check and clean (plan, 1.4)

Goal: the AI can check the save the player is playing on, explain what's wrong in it, and with the player's agreement clean
it and put them back in the game on the cleaned save. Cleaning only takes effect by loading the cleaned file, so the
workflow always ends with a load.

Inspired by ReSaver (FallrimTools, by Mark Fairchild, Apache-2.0), which remains the reference: its source ships with the
installed tool, and `scripts/save-scan/` already drives its library headlessly.

## Status (2026-11-19)

Built: the native scan (matches ReSaver's counts on six saves), `ListSaves`, `CheckSave`, `CleanSave` and `CleanAndReload`
(cleaning runs ReSaver's own library through `SkyrimMCP/SaveClean/SaveClean.java`). Not yet tested in game.

## When the AI offers it

The server instructions tell the AI: when the player says the game is slow, stuttering or the FPS are dropping, offer
`StackReport` first, then `CheckSave`; explain the findings naming the mods, and talk about those mods (what they do, whether
they're still needed, settings, updates, known conflicts). If the player develops mods, ask which ones and tie the findings back
to that work. Before any clean, say that it edits saved game data, what could break, that the original save is kept as the way
back, and that a pile-up returns if the mod keeps causing it. The cleanup cycle (`CleanAndReload`) saves, cleans a copy and
loads it in one go after the player agrees.

## Tools

| Tool | What it does | Writes? |
|---|---|---|
| `CheckSave(saveName?)` | With no name: makes a fresh save of the current game (`SkyLink_Check_<time>`), waits until the file is complete, then scans it. With a name: scans that save. Reports the findings in plain language with counts and the scripts or mods involved. | One new save (the check save), nothing else |
| `CleanSave(saveName, levels="safe", dryRun=true)` | Removes the chosen findings from a **copy** (`<name>_cleaned`), never the original. `levels`: safe (default), caution; risky items only when named one by one. Dry run lists what would go and what could break. | A new save file |
| `CleanAndReload(what, confirm)` | The in-game flow: `CheckSave` (fresh save), `CleanSave` to a copy, load the copy, wait for the load, run `CheckSave` again on a new save to confirm the problems are gone. Needs the player's confirmation and the move safety rules (no menus, scenes, combat). | Two new saves and the cleaned copy |
| `ListSaves(filter?)` | The save folder of the running game (Steam or GOG), newest first, with character ID, name, level, location, time. | No |

## What the scan reports (ReSaver's checks)

- **Undefined scripts**: script data in the save with no script file installed (a mod removed or renamed scripts).
- **Unattached instances**: script instances bound to nothing (usually left by removed or updated mods; some are normal).
- **Undefined elements and threads**: active scripts, suspended stacks and messages that refer to undefined scripts.
- **Size and health**: broken or truncated Papyrus section, string table size, change form count, save size against the
  limits that cause problems.
- Grouped by plugin where the script can be traced to one, so the reply says "left over from <mod>" rather than raw names.

## Script stacks (the main use)

Stacks are the main thing Cory cleans: they pile up and **destroy FPS**. A stack is a script call in progress; when a mod
loops, waits on something that never comes, or keeps starting the same handler, hundreds of copies of one function sit in
the save and the live game, all competing for the script engine's time each frame.

**Live first (no save edit):** `GetScriptStats` already reads the running Papyrus VM: running stacks, latent waits,
overstress, and the busiest scripts, entry points and functions. A new `StackReport` builds on it:
- Samples the VM for a few seconds and reports stack counts per script and function, how fast they grow, and the
  plugin each script comes from.
- Flags **runaway stacks**: many copies of the same function (e.g. 20 or more), a count that keeps growing between
  samples, or stacks waiting on latent calls for a long time. Shows overstress and per-frame script load next to it so
  the FPS link is visible.
- Says what it means in plain language ("<mod>'s OnUpdate is running 340 times at once and growing").

**Cleaning stacks (save edit and reload):** the engine can't kill a running stack from outside safely, so cleaning
terminates them in a copy of a fresh save, then loads it (`CleanAndReload`). Stack findings get their own levels:

| Stack finding | Level | Notes |
|---|---|---|
| Stacks of scripts **no longer installed** | Safe | Nothing can finish them |
| **Runaway duplicates** of an installed script (same function, many copies, growing) | Caution | The usual FPS killer. Terminating them stops the pile-up; the mod re-runs its handler on its next event. Warn that the mod may lose whatever that call was doing, and that the pile-up will return if the mod keeps looping (then the mod itself is the fix: update, configure or remove). |
| A few stacks of an installed script, not growing | Risky | Probably doing real work; listed only |
| Stacks of **quest scripts** (attached to a running quest), especially main quest and faction lines | Risky | Killing one can leave the quest stuck at a stage; item by item only, with the quest named |
| Base game and DLC script stacks | Never | Reported only |

After a stack clean the tool re-checks live: `StackReport` again after the reload, to show whether the pile-up is gone
and whether it is coming back.

## What is safe to clean

Every finding gets a risk level, and the reply says what could break for each one before anything is cleaned.

| Level | Findings | Why | Default |
|---|---|---|---|
| **Safe** | Script data, instances, threads and messages belonging to scripts whose files are **no longer installed** (undefined), from a mod the player removed | Nothing in the game can run them any more; they only take space and cause errors on load | Offered; cleaned with one confirmation |
| **Caution** | **Unattached instances of scripts that are still installed** | Often harmless leftovers, but a mod may still expect them (some mods keep instances on purpose, and an update mid-playthrough can leave them); removing one can stop that mod working until it re-initialises | Listed with the mod name; cleaned only if the player picks them, with a warning |
| **Risky** | **Active or suspended script threads** (stacks) of installed scripts; data of scripts attached to a **running quest**, especially the main quest and faction lines | A thread is a script in the middle of running; killing it can leave a quest stuck at a stage or a mod half-configured, and the break may only show hours later | Listed, never part of "clean all safe"; each needs explicit confirmation and a warning naming the quest or mod |
| **Never** | Base game and DLC scripts and change forms, the string table, anything the scanner can't attribute | Removing these breaks the game or the save outright | Reported only; the tool refuses to remove them |

Rules that go with the levels:
- A finding is only **Safe** when its script file is confirmed missing from the running game (loose and in archives), not just unknown to the scanner.
- Each finding names its plugin when it can be traced, and whether that plugin is installed and enabled. A plugin that's still installed moves its findings out of Safe.
- The dry run (default) prints, per level, the count, the mods involved and the possible breakage; `CleanSave` takes the levels to clean (`safe` by default) and refuses Risky items unless they are named one by one.
- After cleaning, the player is told the original save is the way back, and to play a little and make a fresh save before deleting it.
- The scan also warns about things cleaning can't fix: a mod updated mid-playthrough against its author's advice, a very large save, or a string table near its limit.

## Safety

- Never overwrites a save; cleaned output is always a new file, and the original is left untouched as the backup.
- Only works on complete files: waits for the save to finish writing (the game saves asynchronously, 10+ seconds with a
  heavy load order) before reading it.
- Loads only through the existing `LoadSave` and only when the move rules allow it (FEAT-259/260).
- Cleaning defaults to what ReSaver treats as safe (unattached instances, undefined elements). Terminating threads and
  removing script data of installed mods are opt-in, with a warning.
- Reads the save folder of the running game (BUG-088), and the character ID ties check results to the character (BUG-090).

## Build order

0. **`StackReport` (live)** on top of `GetScriptStats`: per-script and per-function counts, growth between samples,
   plugin, runaway flags. No save involved, so it ships first and is useful on its own. About 1-2 hours.
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

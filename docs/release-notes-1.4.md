# SkyLink AI v1.4.0 (draft, not released)

Save health and character transfer. The AI can now see what slows a game down (script pile-ups, live and in the save), clean
a save and put the player back in the game on the cleaned copy, check whether an old save survives a collection update, and
carry a character's skills, perks, spells, shouts and quest progress to another character.

## Performance and save health

- **StackReport.** Samples the script engine for a few seconds and names every pile-up by handler and mod: running and
  queued copies together ("about 11,364 copies: 11,012 waiting to start, 352 running"), classed as a burst, periodic sweep,
  runaway, creeping leak or stuck high, with a history across calls that resets when a save loads. Warns when the game is
  paused (frozen counts look calm).
- **CheckSave and ListSaves.** SkyLink reads saves itself: a port of ReSaver's reader (FallrimTools by Mark Fairchild,
  Apache-2.0), checked against ReSaver on 26 saves with identical counts, about 0.5 s a save. Reports queued stacks and
  threads per mod, script leftovers of removed mods, unattached instances and plugins no longer loaded.
- **CleanSave and CleanAndReload.** Clean a copy, never the original, using an installed ReSaver's own library: leftovers
  of removed mods (safe), unattached instances (caution), and named pile-ups or a whole mod's. CleanAndReload saves, cleans,
  loads the copy and checks whether the pile-up returns; if the mod causing it is still running it says so.
- **CompareSaveToLoadOrder.** For collection updates: which of a save's plugins are gone, renamed (to the game, a removal),
  changed between full and light, added or reordered, and how much of the save each removed one owns. With `scripts: true`
  it also compares every script's variables in the save with the installed script files (loose and in BSAs): removed or
  retyped variables are what breaks a save after a mod update.
- **ArchiveSaves** moves test and way-back saves to `Saves\SkyLink_Archive`; nothing is deleted.
- The AI offers these when the player says the game is slow or the FPS drop, talks through the mods involved (and, for mod
  authors, ties findings to their own work), and explains the risks before any clean.

## Character transfer

- **ApplyBlueprint** adds `skills`, `level`, `perks`, `spells` and `shouts` (with unlocked words): add-only, dry run first.
- **Quest progress.** Blueprints record the stages done for each quest, read from a save (the engine's in-memory list was
  unreliable). **ReplayQuests** starts a quest if needed and sets each missing stage in order, checking every step; main
  and faction lines need explicit permission.

## Fixes

- Loading a save made with the console `save` command (SkyLink's own saves before 1.4) dropped the character ID to
  00000000 for every later save. SkyLink now keeps the ID in its co-save record and restores it after a load;
  **RepairCharacterId** fixes games already affected (BUG-091).
- Saves sheathe a drawn weapon or spell first (it blocks saving in some load orders) and say plainly when a mod refused one.
- Per-game folders for Steam and GOG installs run side by side (BUG-088); per-character settings keyed by the save system's
  character ID (BUG-090); RaceMenu-less games get a clear message from RaceMenu tools (BUG-089); a stalled game thread is
  reported as such (BUG-087).
- Console moves (`coc`, `cow`, `player.moveto`) wait out the loading screen (BUG-024); captures warn about dark light and
  covered foreheads (BUG-086); the crash recorder keeps five unclean sessions (FEAT-264).
- GOG Skyrim 1.6.1179 tested (BUG-012).

## Requirements

Unchanged from 1.3. Cleaning saves needs ReSaver (FallrimTools) installed; everything else works without it.

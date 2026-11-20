# SkyLink AI v1.4.0

Save health and character transfer. The AI can now see what slows a game down (script pile-ups, live and in the save), clean
a save and put the player back in the game on the cleaned copy, check whether an old save survives a changed load order, and
carry a character's skills, perks, spells, shouts and quest progress to another character. Teleports show the game's own
loading screen.

## Performance and save health

- **StackReport.** Samples the script engine for a few seconds and names every pile-up by handler and mod, running and
  queued copies together ("about 11,364 copies: 11,012 waiting to start, 352 running"), classed as a burst, periodic sweep,
  runaway, creeping leak or stuck high, with a history across calls that resets when a save loads. Warns when the game is
  paused (frozen counts look calm).
- **CheckSave and ListSaves.** SkyLink reads saves itself: a port of ReSaver's reader (FallrimTools by Mark Fairchild,
  Apache-2.0), checked against ReSaver on 26 saves with identical counts, about 0.5 s a save. Reports queued stacks and
  threads per mod, script leftovers of removed mods, unattached instances and plugins no longer loaded. `ListSaves(scan)`
  flags saves holding a stack flood.
- **CleanSave and CleanAndReload.** Clean a copy, never the original, with an installed ReSaver's own library: leftovers
  of removed mods, unattached instances, and named pile-ups or a whole mod's. CleanAndReload saves, cleans, loads the copy
  and checks whether the pile-up comes back; if the mod causing it is still running it says so.
- **CompareSaveToLoadOrder.** For collection updates: which of a save's plugins are gone, renamed (to the game, a removal),
  changed between full and light, added or reordered, how much of the save each removed one owns, and (`scripts: true`)
  which scripts changed their variables under the save, read from loose files and BSAs, Vortex or MO2.
- **ArchiveSaves** moves test and way-back saves to `Saves\SkyLink_Archive`; nothing is deleted.
- When the player says the game is slow or the FPS drop, the AI offers these, talks through the mods involved (and, for mod
  authors, ties findings to their own work), and explains the risks before any clean.

## Character transfer

- **ApplyBlueprint** adds `skills`, `level`, `perks`, `spells` (castable kinds by default) and `shouts` with the words the
  other character had unlocked: add-only, dry run first. The reply names quests the apply set off (a mage's ward practice),
  spells or shouts another mod granted on top, and restores controls a script switched off meanwhile.
- **Quest progress.** Blueprints record the stages done for each quest, read from a save made at export. **ReplayQuests**
  starts a quest if needed, skips one whose required roles stay empty, and sets each missing stage in order, checking every
  step; main and faction lines need explicit permission.

## Other additions

- **Teleports show the loading screen**: the player is moved to a marker in the target cell, as the way back always did.
- **MCM Recorder**: `ListMcmRecordings` and `PlayMcmRecording` play recorded MCM settings; playing or recording blocks
  moves, captures and face edits.
- **GetKnownShouts** shows each word's status and readable spelling; unlocking a word adds its shout.
- **RepairCharacterId** fixes games whose character ID was lost.

## Fixes

- Loading a save made with the console `save` command (SkyLink's own saves before 1.4) dropped the character ID to
  00000000 for every later save. SkyLink keeps the ID in its co-save record and restores it after a load.
- Saves sheathe a drawn weapon or spell first and say plainly when a mod refused one.
- An OStim scene is recognised by its menu (OStim's API could report none); `StopIdle` under one crashed OStim.
- Face edits wait while the Journal or MCM is open.
- A wrong parameter name gets a message naming it and the valid ones.
- A game that crashed or closed is reported as such instead of a run of timeouts.
- Quest status: a stopped quest no longer reads as running; stage checks wait for slow final stages.
- Steam and GOG installs side by side (per-game folders); per-character settings keyed by the save system's character ID;
  RaceMenu tools explain when RaceMenu isn't installed; a stalled game thread is reported as such.
- Console moves wait out the loading screen; captures warn about dark light and covered foreheads; the crash recorder keeps
  five unclean sessions.
- Light plugins numbered outside FE (plugin extenders) resolve in blueprints.
- GOG Skyrim 1.6.1179 tested.

## Requirements

Unchanged from 1.3. Cleaning saves needs ReSaver (FallrimTools) installed; everything else works without it.

## Licences

Unchanged: the plugin DLLs are GPL-3.0-or-later (CommonLibSSE-NG), the server and launcher MIT. The save reader follows
ReSaver's (Apache-2.0); see `SkyLinkAI_Server\NOTICE-ReSaver.txt`.

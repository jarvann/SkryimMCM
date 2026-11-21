# SkyLink AI v1.5.0

Mod building, crash care and runaway scripts. The AI can now patch a mod's script and package the fix as a small override
mod, write small plugins, check and install them into Vortex or MO2 and sort with LOOT; read crash logs against known
patterns and Windows crash dumps; read LOOT's notes on your plugins and clean dirty ones with SSEEdit; stop a runaway script
loop for the session; and restart the game itself when a change needs it. Tested on Steam 1.7.104 (a large Vortex collection) and
GOG 1.6.1179 (vanilla, through MO2).

## Mod building

- **GetCreationKit** finds an installed Creation Kit and its Papyrus compiler, and offers to unpack the base game's script
  sources once (kept per machine, shared by Steam and GOG). Without the Creation Kit, it explains what installing it (free)
  would add.
- **GetScriptSource** reads a mod's `.psc`, loose or from its BSA, and names the Vortex or MO2 mod it comes from.
- **CompileScript** runs the compiler with the imports worked out: the patch's own folder, Data, the base game's sources and
  the sources of the mods it depends on (MO2-aware). Errors come back with file and line.
- **PackageOverrideMod** packages a compiled fix with its source as a small mod for Vortex or MO2 that wins over the
  original, and installs it straight into an MO2 instance when MO2 is in use.
- **CreatePlugin** writes small plugins (ESP, or light ESPFE when eligible) with correct masters. **GetPluginInfo** and
  **CheckPluginDependencies** read any plugin's header, masters and records. **GetLoadOrderLimits** says how close the load
  order is to 254 full and 4,096 light plugins and whether a plugin can be light. The plugin reader and writer are SkyLink's
  own, written from the public file format documentation.
- **InstallPlugin** checks every master first, then adds the plugin to Vortex's or MO2's load order (dry run first);
  **SortLoadOrder** runs LOOT.

## Crash and runaway care

- **GetCrashLog** matches Crash Logger reports against known patterns (the likely mod and the fix), lists the plugins and
  meshes a report names, warns when RAM or VRAM was nearly full, and suggests
  [Phostwood's Crash Log Analyzer](https://phostwood.github.io/crash-analyzer/) as a second opinion. It also reads Windows
  crash dumps for crashes Crash Logger misses (quitting, returning to the main menu), ties a report to SkyLink's crash
  recorder session, and only shows the connected game's files (Steam and GOG side by side).
- **GetLootNotes** reads the masterlist your LOOT keeps (never shipped with SkyLink): incompatibilities, missing
  requirements, warnings, and which plugins are dirty for their exact file.
- **CleanPlugins** cleans what LOOT lists as dirty with your installed SSEEdit's Quick Auto Clean, with the game closed:
  backup first, a cleaning log, and a check against LOOT's clean checksum.
- **StopRunaway** ends a runaway update loop (a script registering for updates faster than its copies finish) for the
  session: it removes the registrations straight from the script engine and blocks them until the next load, then StackReport
  shows the pile drain. It says plainly when a pile-up is an event flood it can't stop, and points to the mod.
- **StackReport** labels pile-ups across calls: creeping, climbing fast (with the rate), stuck high, draining, or spiking.
- **RestartGame** saves, quits, and starts the same game again through SKSE or MO2; **StartNewGame** starts from the main
  menu, straight into a cell if asked.
- A quit SkyLink asked for (RestartGame or a console quit) is labelled as such in the crash recorder, not as a crash.

## Other additions

- **GetJournal** returns what the quest journal shows: log entries for the stages done and the objectives as displayed, with
  `[Role]` names resolved. It names any journal or diary mod installed, so the AI can use it.
- **ExecuteConsoleCommand** returns the console's full output, line by line.
- **GetSkyLinkCost** shows how much game-thread time SkyLink's own calls use.
- Several AI clients can connect at once (up to four).
- Meshes are read from BSA archives too: the hair picker and the sculpt brush work with vanilla and packed hair and eyes.

## Fixes

- When the game window is in the background and Skyrim.ini doesn't keep the game running there (bAlwaysActive), calls say
  so instead of timing out.
- A save refused during combat, while overloaded or with a weapon drawn says why.
- LoadSave refuses a save name that doesn't exist and lists close matches.
- Quest objectives no longer show raw `<Alias=...>` tokens.
- ApplyBlueprint adds equipment before equipping it, and skips spells the character doesn't know.
- GetMenuState lists mods' own menus; face edits wait while one is open.
- A late reply after a timeout can no longer be read as the next call's answer.
- Face retries pause during loading screens.

Updating from 1.4: the plugin DLL changed, so restart Skyrim after installing. New tools appear after Claude Desktop is quit
from the system tray and opened again.

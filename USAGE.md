# SkyLink AI — Usage Guide

## Getting Started

1. Launch Skyrim via SKSE loader
2. Wait for the game to fully load (main menu or in-game)
3. Open Claude Desktop
4. Verify connection: *"Is Skyrim connected?"*

The MCP server automatically connects when both Skyrim and Claude Desktop are running.

## Basic Commands

### Player Info
```
"What's my health and level?"
"What are my skill levels?"
"What perks do I have?" (page 1)
"How many spells do I know?" (summary only, no data loaded)
"What am I wearing?"
"What do I look like?" (face morphs, head parts, race)
"What factions am I in?"
"What are my magic resistances?"
"Am I a vampire?"
"What powers do I have?"
```

### Inventory
```
"What's in my inventory?" (paged — page 1, 50 items)
"Show me inventory page 3"
"How many items do I have?" (summary only)
"Give me 5000 gold"
"Give me 10 iron swords"
"Remove 5 lockpicks"
"What enchantments are on Potema's Crown?" (pass FormID)
```

### Equipment
```
"What am I wearing?" (all 32 biped slots)
"Equip the iron sword"
"Unequip my helmet"
```

### Quests
```
"What quests am I on?"
"Show me the stages of quest 00074793"
"Who fills the aliases in the main quest?"
"What quest items do I have?"
"Set quest stage to 200"
"Start quest / stop quest"
```

### NPCs & Followers
```
"Who's nearby?"
"Who are my followers?"
"Tell me about Inigo" (detailed NPC info)
"What is Inigo carrying?"
"Can that guard see me?" (detection level)
"What am I looking at?" (crosshair target)
"Kill that NPC" (by refId)
"Move Lydia to me"
```

### World
```
"Teleport me to Whiterun"
"What's the weather?"
"Make it snow"
"List all weather types"
"Where exactly am I?" (cell info + coordinates)
"What objects are nearby?" (containers, doors, furniture)
"What's in that chest?" (container inventory)
"Unlock that door"
"Discover all map markers"
"What time is it in-game?"
"Set time to noon"
```

### Combat
```
"Am I in combat?" (targets + allies)
"What's my damage and armor rating?"
"Any threats nearby?" (hostile actors with weapons)
"Toggle god mode"
"Toggle immortal mode" (takes damage, can't die)
"Toggle collision" (noclip)
```

### Economy
```
"Any merchants nearby?"
"What does the merchant sell?"
"Do I have any bounties?"
"Clear my bounty with The Pale"
```

### Magic
```
"Tell me about the spell Fireball" (search first, then details)
"What enchantments are on my ring?"
"What are my resistances?"
"Am I diseased?"
"What daily powers do I have?"
```

### Save & Load
```
"Save my game" (sheathes a drawn weapon or spell first; says so if a mod refused the save)
"Take a snapshot" (blueprint + save with matching names)
"Load my most recent save"
"Load save named MyTestSave"
"List my saves" / "Which of my saves still have a stack flood?" (ListSaves, scan: true)
"Tidy up the test saves" (ArchiveSaves: moves them to Saves\SkyLink_Archive, never deletes)
```

### Performance and Save Health (1.4)
```
"The game is getting slow" / "My FPS are dropping"
  -> StackReport: live script pile-ups for a few seconds, named by handler and mod, with a history across calls
  -> CheckSave: saves now and scans the save (queued stacks, script leftovers of removed mods, plugins no longer loaded)
"Clean the Aniya stacks out of my save"
  -> CleanAndReload (preview first): saves, cleans a copy with ReSaver's library, loads it, and checks whether the
     pile-up comes back. The original save is kept as the way back. If the mod causing it is still running, it says so:
     clean AND stop the cause (update, settings, stop its quest), or it returns within a minute.
"Will this save survive the collection update?"
  -> CompareSaveToLoadOrder: removed, renamed, retyped and added plugins, and how much of the save each removed one owns
"My character ID is 00000000" -> RepairCharacterId
```
Cleaning needs ReSaver (FallrimTools) installed; SkyLink finds it in the usual places or via SetSkyLinkSetting('resaverPath').
Editing a save is risky: SkyLink never writes over a save, and the AI should explain what could break before it cleans.

### Events
```
"What happened recently?" (poll events)
"Show me only combat and death events"
"Poll events excluding lock changes"
```

### Utility
```
"Search for iron sword" (form database search)
"What's the FormID prefix for HLIORemi.esp?"
"Show me the load order"
"What SKSE plugins do I have?"
"Is the game in a safe state?"
"What menus are open?"
```

### Papyrus Bridge
```
"What Papyrus functions are available?" (scans 7400+ source files)
"What can ConsoleUtil do?" (function signatures)
"What can the Experience mod do?"
```

### Loot & Analysis
```
"Scan for valuable loot nearby"
"Export my character blueprint to Downloads"
"What's my damage and armor compared to nearby threats?"
```

## Advanced Usage

### Paging Large Results

Tools that return many items support `page` and `pageSize` parameters:
- `page=1, pageSize=50` — default, returns first 50 items
- `page=0` — summary only (just the total count, no data)
- `page=3, pageSize=25` — third page of 25 items

This saves tokens and context window space. Ask *"How many spells do I have?"* instead of *"Show me all my spells"*.

### Character Blueprint

The blueprint is a complete character snapshot exported to a JSON file:
- Player info, skills, perks, spells, shouts
- Equipment, full inventory with enchantments, gold
- Appearance (face morphs, head parts, race, height)
- Quest progress (1.3): quests completed or started, with stage and the objectives seen; from 1.4 also the stages done,
  read from a save made at export, and which shout words were unlocked
- Factions, resistances, powers, diseases, damage stats
- `notCaptured` lists what it can't hold (quest rewards and world changes, map markers, crime, other mods' script state)

Always use `outputPath`: the data is too large for the MCP response.

`ApplyBlueprint` (dry run by default) adds missing items and gold and equips the blueprint's gear on another character. From
1.4 the sections `skills`, `level`, `perks`, `spells` and `shouts` raise and add what this character lacks (nothing is ever
lowered or removed; perk points and level-up choices aren't given). With `sections: "quests"` it compares quest progress.

`ReplayQuests` (1.4) brings quest progress across, quest by quest: `quests: "list"` shows candidates; a preview makes a
way-back save and shows the stages to set; confirming starts the quest if needed and sets each missing stage in order,
running its script as if played, and stops at a stage the game refuses. Main, faction, Civil War and DLC lines need
`allowMajor: true`. Simple quests first, after a save.

### Event Filtering

Poll events supports filters to reduce noise:
- `eventTypes` — only return specific types: `"combat,death,quest_stage"`
- `excludeTypes` — exclude noisy types: `"lock_changed,quest_start_stop"`

Framework quest noise (NFF cycling, etc.) is auto-filtered.

### Console Command Fallback

`ExecuteConsoleCommand` is a last resort. Always prefer dedicated tools:
- Instead of `player.getav health` → use `GetPlayerInfo`
- Instead of `sqv questId` → use `GetQuestStages` + `GetQuestAliases`
- Instead of `help "iron" 0` → use `SearchForms`

When using console commands, target NPCs with dot-notation: `FE124B0E.kill` (not `prid`). Quote a reference that starts
with a digit: `"61033226".moveto player`. To set a quest stage, use `SetQuestStage`: it reports whether the game applied it,
which `setstage` in the console doesn't.

### Game Safety

Tools check what they need themselves and refuse with the reason. `GetGameSafety(action: "move" | "camera" | "face" | "actor")`
answers the same way in advance; with no action it lists the raw states (menus, scenes, OStim, furniture, mount...). Reads
are never blocked. For a picture during a scene or in a chair, use `TakeScreenshot`.

## Face Builder (experimental)

Needs RaceMenu. The AI can measure the character's face, compare it with a photo or drawing, and edit it until they match.
A typical session:

1. **Studio:** `StudioGo` takes her to a quiet room with fixed lights (`StudioReturn` brings her back to the exact spot).
   Measurements outdoors vary with light and weather.
2. **Reference:** `SetFaceReference(image: "<path to photo>")` registers the face to aim for (several pictures of one person
   work better than one).
3. **Measure:** `MeasureFace(reference: "default")` lists every proportion and colour with the gap to the reference;
   `FaceSideBySide` shows both.
4. **Edit:** `SetFace` (sliders, makeup tints, head parts in one load), `SetBrowColor`, `SetHairColor`, `SculptFace` for
   shapes no slider reaches. `ProbeSlider` measures what a slider actually does on this head.
5. **Repeat** 3 and 4, then save a preset.

Hair: `FindHairstyles(length: "shoulder", fringe: true)` shows contact sheets of every hair the character menu offers,
measured from the mesh files, so styles can be compared without loading each one in game.

Limits: proportions can match closely, but a likeness also depends on soft tissue and hairstyle; body silhouette widths
and arm length are reported, not compared. Changing head parts on followers with pre-built faces is refused (their face
breaks after a restart).

## Troubleshooting

### "Cannot connect to Skyrim"
- Ensure Skyrim is running via SKSE (not the vanilla launcher)
- Check that `SkyrimMCPPlugin.dll` is in `Data/SKSE/Plugins/`
- Verify Address Library for SKSE is installed

### Console commands not executing
- Check the SKSE plugin log: `Documents/My Games/Skyrim Special Edition/SKSE/SkyrimMCPPlugin.log`
- Use dedicated tools instead of console commands when possible

### Game crashes on command
- Some console commands are unsafe during certain game states
- Use `GetGameSafety` before risky operations
- Commands during loading screens, kill moves, or active saves may crash

### MCP server timeout
- Heavy operations (save game, character blueprint) may take 10+ seconds
- Save game is async — returns immediately, completes in background
- Blueprint export uses `outputPath` to write directly to disk

### Events too noisy
- Use `excludeTypes` to filter out lock_changed and quest_start_stop
- Framework quest noise is auto-filtered but some may still appear

### Tool not found
- Close and reopen Claude Desktop to reload the tool list
- C# changes don't need a game restart, just a Claude Desktop restart
- C++ changes require a full game restart

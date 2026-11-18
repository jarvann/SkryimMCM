# SkyLink AI

A Model Context Protocol (MCP) server that lets AI assistants (Claude, etc.) interact directly with Skyrim SE/AE's runtime engine. About 150 tools for reading and changing game state in real time: player stats, inventory, quests, NPCs, world, combat, and (new in 1.3, experimental) building and matching a character's face.

## Architecture

```
Claude Desktop <--stdio--> SkyLinkLauncher <--stdio/MCP--> C# MCP Server <--named pipe--> SKSE plugin (inside Skyrim)
```

**SKSE plugin** (C++, CommonLibSSE-NG): a small host DLL loaded by SKSE and a logic DLL it loads and can swap while the game runs. Exposes the game actions over a named pipe (`\\.\pipe\SkyrimMCP`).

**C# MCP server** (.NET 10): bridges Claude Desktop (stdio MCP) and the plugin. The launcher restarts it when a new version is installed. Face and body measurement run here, locally, with ONNX Runtime and Google's MediaPipe models (no internet, no account).

## Features

| Category | Tools |
|----------|-------|
| **Player** | Info, skills, perks, spells, shouts, appearance, factions, resistances, powers, diseases, level, blueprint export (now with quest progress) and apply (inventory, equipment, gold) |
| **Inventory** | Full inventory (paged), equipped items, add/remove, bulk add, equip/unequip, enchantment details, loot by category |
| **Quests** | Active quests, stages, aliases (resolved to NPCs), quest items, verified set stage, start/stop/complete |
| **NPCs** | Nearby NPCs, detailed info, NPC inventory and appearance, followers, detection, crosshair targeting, move to the player |
| **World** | Weather, cell info, nearby objects, containers, doors, map markers, teleport (with a way back) |
| **Combat** | Combat state, damage stats, threat assessment |
| **Face builder** *(experimental, needs RaceMenu)* | Repeatable face captures, measurement against a photo, sliders, makeup tints, brow and hair colour, head parts, sculpt brush, slider probing, a lit studio room, side-by-side sheets |
| **Hair and body** *(experimental)* | Hair picker by shape (contact sheets, no in-game cycling), body proportions, RaceMenu body morphs and overlays (OBody-aware) |
| **Utility** | Console commands with output, form search, load order, volume (with snapshot and restore), screenshots, crash log and SkyLink's crash recorder, Skyrim guidebook (UESP), notes and goals |
| **Papyrus** | Function catalog, function lookup, VM call bridge |

### Safety

Each kind of action has its own rule, applied by the tool itself, and `GetGameSafety(action)` answers the same way in advance:

- **Moving the player** (teleport, studio trips) is refused during menus (including ones a mod keeps from pausing), combat, dialogue, scenes, OStim scenes, script-held controls, furniture, mounts, ragdoll, falling, bleeding out or carrying an object.
- **Captures and measurements** are refused in scenes, menus, combat or furniture (a capture's pose reset would stand her up); `TakeScreenshot` always works.
- **Face edits** wait for scenes, a mod's menu or a message box, and beast form.
- Reads (inventory, quests, morphs...) are never blocked. Every game-thread task runs inside a fault guard.

### Other

- **Paged output** for large lists; `page=0` returns a count-only summary
- **Character blueprint**: one call exports the whole character, with portable form IDs that survive a load-order change
- **Event system**: 17 event types with noise filtering
- **Hot reload**: both the plugin's logic and the server update while the game and Claude Desktop keep running

## Requirements

- **Windows 10 or 11**
- **Skyrim SE or AE** — Steam 1.7.104 (SKSE64 2.3.1), 1.6.1130–1.6.1170, or SE 1.5.97
- **SKSE64** ([skse.silverlock.org](https://skse.silverlock.org/))
- **Address Library for SKSE** ([Nexus](https://www.nexusmods.com/skyrimspecialedition/mods/32444))
- **.NET 10 Runtime** ([download](https://dotnet.microsoft.com/download/dotnet/10.0))
- **Claude Desktop** ([claude.ai/desktop](https://claude.ai/desktop))
- **RaceMenu** (optional): needed for the face builder, presets, sculpting, body morphs and overlays. The native appearance tools (hair colour, skin tone, head parts) work without it.

## Installation

1. Install with MO2 or Vortex, or extract the zip into your Skyrim `Data` folder (the zip starts at `SKSE\`)
2. Install .NET 10 Runtime if not already installed
3. Configure Claude Desktop (see below)
4. Launch Skyrim via SKSE, then open Claude Desktop

## Claude Desktop Configuration

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "skyrim": {
      "command": "dotnet",
      "args": ["<SKYRIM_PATH>\\Data\\SKSE\\Plugins\\SkyLinkAI_Launcher\\SkyLinkLauncher.dll"]
    }
  }
}
```

Replace `<SKYRIM_PATH>` with your Skyrim installation path.

**Mod Organizer 2:** Claude Desktop runs outside MO2, so it can't see MO2's virtual Data folder. Point `args` at the mod's own folder instead, for example `<MO2_PATH>\mods\SkyLinkAI\SKSE\Plugins\SkyLinkAI_Launcher\SkyLinkLauncher.dll`.

The launcher runs the MCP server (`SkyLinkAI_Server\SkyrimMCP.dll`) and restarts it when a new version is installed, so updating SkyLink doesn't need a Claude restart. Changed behaviour applies on the next call. Claude Desktop reads the tool list once when it starts, though, so new tools and changed tool descriptions only appear after you quit Claude Desktop (from the system tray) and open it again. A new chat isn't enough. Pointing `args` straight at `SkyLinkAI_Server\SkyrimMCP.dll` also works, as it did before 1.2.1.

**Updating with the game running:** the plugin's command handlers live in `SKSE\Plugins\SkyLinkAI\SkyLinkLogic.dll`, which SkyLink reloads on the next call after a new version is installed (or ask Claude to run `reload_plugin`). Only a new `SkyrimMCPPlugin.dll` needs a game restart.

## Usage

1. Launch Skyrim via SKSE
2. Open Claude Desktop
3. Just ask. Anything.

```
"Give me 5000 gold and teleport me to Solitude"
"What's the strongest NPC nearby and what are they carrying?"
"Export my character blueprint to a file"
"Scan for valuable loot in nearby containers"
"Make it snow, then toggle god mode"
"Find the Remiel quest and show me who fills each alias"
"Spawn 5 bandits and monitor the combat"
"What Papyrus functions does the Experience mod expose?"
```

With 89 tools covering player state, inventory, quests, NPCs, world interaction, combat, economy, magic, events, and a Papyrus VM bridge to 3,000+ mod functions — if Skyrim can do it, Claude can probably help.

See **[USAGE.md](USAGE.md)** for the full tool reference and advanced features.

## Building from Source

```powershell
# Prerequisites: VS2022 C++ workload, CMake 3.21+, vcpkg, .NET 10 SDK

$env:VCPKG_ROOT = "C:\vcpkg"

# Clone CommonLibSSE-NG (one-time)
git clone --depth 1 --branch v16.3.0 https://github.com/alandtse/CommonLibVR.git SkyrimMCPPlugin/extern/CommonLibSSE-NG

# Build both plugin DLLs (SkyrimMCPPlugin.dll + SkyLinkAI\SkyLinkLogic.dll); -Deploy copies them into Skyrim.
# See build_plugin.ps1 for why it builds from a local mirror of the sources.
.\build_plugin.ps1

# Build MCP server and launcher (-ServerOnly publishes them into Skyrim)
dotnet build SkyrimMCP
dotnet build SkyLinkLauncher

# Or build release package
.\build_release.ps1 -Version "1.3.0"
```

## Project Structure

```
├── SkyrimMCPPlugin/          # C++ SKSE plugin
│   ├── src/
│   │   ├── Main.cpp          # SKSE entry point
│   │   ├── PipeServer.*      # Named pipe server
│   │   ├── Protocol.*        # Command registry
│   │   ├── Helpers.h         # Shared utilities + ResolveActor
│   │   ├── PlayerQueries.*   # Player state + toggles
│   │   ├── NPCManager.*     # NPC/actor operations
│   │   ├── InventoryManager.*# Item operations
│   │   ├── QuestManager.*    # Quest operations
│   │   ├── WorldManager.*    # World interaction
│   │   ├── CombatAnalysis.*  # Combat analysis
│   │   ├── UIManager.*       # Menu state
│   │   ├── UtilityManager.*  # Misc utilities
│   │   ├── PapyrusBridge.*   # Papyrus VM bridge
│   │   ├── EventSystem.*     # Game event sinks
│   │   └── TaskQueue.*       # Game thread dispatch
│   ├── CMakeLists.txt
│   └── vcpkg.json
├── SkyrimMCP/                # C# MCP server (.NET 10)
│   ├── Program.cs
│   ├── IPipeClient.cs
│   ├── PipeClient.cs
│   ├── SkyrimOffsets.cs
│   └── Tools/                # MCP tool classes
│       ├── ToolBase.cs
│       ├── PlayerTools.cs
│       ├── InventoryTools.cs
│       ├── QuestTools.cs
│       ├── NPCTools.cs
│       ├── WorldTools.cs
│       ├── CombatTools.cs
│       ├── UtilityTools.cs
│       └── PapyrusTools.cs
├── build_release.ps1         # Nexus packaging script
├── project_work_items.json   # Feature/bug tracking
├── project_dashboard.html    # Visual project dashboard
├── ROADMAP.md               # Feature roadmap
├── CHARACTER_BLUEPRINT_SPEC.json
└── CLAUDE.md                # Dev guidance for Claude Code
```

## Credits

- Console command execution approach adapted from [ConsoleUtilSSE](https://github.com/VersuchDrei/ConsoleUtilSSE) by VersuchDrei (MIT License)
- [CommonLibSSE-NG](https://github.com/alandtse/CommonLibVR/tree/ng) — alandtse fork (Skyrim 1.7 support), originally by CharmedBaryon and Ryan McKenzie
- SKSE by Ian Patterson, Stephen Abel, and Paul Shortman
- Face and pose landmark models: Google [MediaPipe](https://ai.google.dev/edge/mediapipe) Face Landmarker and Pose Landmarker (Apache-2.0), converted to ONNX unchanged; see `Models\NOTICE.txt`
- [ONNX Runtime](https://onnxruntime.ai/) by Microsoft (MIT)
- Guidebook content from [UESP](https://en.uesp.net/) (CC BY-SA 2.5), fetched on demand

## License

- **SKSE plugin** (`SkyrimMCPPlugin/`, distributed as `SkyrimMCPPlugin.dll`): GPL-3.0-or-later. It statically links CommonLibSSE-NG, which is GPL-3.0-or-later with a modding exception, so the compiled plugin carries that licence.
- **MCP server** (`SkyrimMCP/`): MIT. It is a separate program that talks to the plugin over a named pipe.
- **Models** (`SkyrimMCP/Models/`): Apache-2.0 (Google MediaPipe), with `NOTICE.txt` and the licence text alongside.

Full texts: [`SkyrimMCPPlugin/LICENSE`](SkyrimMCPPlugin/LICENSE) and [`SkyrimMCP/LICENSE`](SkyrimMCP/LICENSE). Source for both is in this repository.

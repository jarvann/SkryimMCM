# SkyLink AI

A Model Context Protocol (MCP) server that lets AI assistants (Claude, etc.) interact directly with Skyrim SE/AE's runtime engine. 74 tools for reading and modifying game state in real time — player stats, inventory, quests, NPCs, world interaction, combat, and more.

## Architecture

```
Claude Desktop <--stdio/MCP--> C# MCP Server <--named pipe--> SKSE Plugin (inside Skyrim)
```

**SKSE Plugin** (C++ DLL) — runs inside Skyrim's process via SKSE. Uses CommonLibSSE-NG for native engine access. Exposes 74 game actions over a named pipe (`\\.\pipe\SkyrimMCP`).

**C# MCP Server** (.NET 10) — bridges Claude Desktop (stdio MCP) and the SKSE plugin (named pipe). 74 MCP tools auto-discovered via `[McpServerTool]` attributes.

## Features

### 74 Game Actions

| Category | Tools |
|----------|-------|
| **Player** | Info, skills, perks, spells, shouts, appearance, factions, resistances, powers, diseases, level, blueprint export |
| **Inventory** | Full inventory (paged), equipped items (32 slots), add/remove, bulk add, equip/unequip, enchantment details |
| **Quests** | Active quests, stages, aliases (resolved to NPCs), quest items, set stage, start/stop/complete |
| **NPCs** | Nearby NPCs, detailed info, NPC inventory, followers, detection level, crosshair targeting, kill, move |
| **World** | Weather (get/set/list 339 types), cell info, nearby objects, containers, lock/unlock doors, map markers, teleport |
| **Combat** | Combat state (targets + allies), damage stats (armor + weapon damage), threat assessment |
| **Economy** | Nearby merchants, merchant inventory, bounties, clear bounties |
| **Magic** | Spell details (cost/school/effects), enchantment info, magic resistances, disease/vampire/werewolf status |
| **Actions** | God mode, immortal mode, collision toggle, save/load game, set time, notifications |
| **Events** | 17 event types: combat, death, quests, location, inventory, equip, hits, spells, activate |
| **Utility** | Console commands (with output capture), form search, load order, SKSE plugin scanner, game safety check |
| **Papyrus** | Function catalog (3,389 functions from 166 scripts), script function lookup, VM call bridge |

### Smart Features

- **Paged output** — large responses (inventory, spells, perks) support page/pageSize params. `page=0` returns count-only summary
- **Character blueprint** — single-call export of entire character state (skills, perks, spells, shouts, equipment, inventory, appearance, factions, resistances)
- **Event system** — 17 event types with framework noise filtering and type/exclude filters
- **Game safety checker** — detects unsafe states (loading, kill moves, saves) before executing commands
- **HUD notifications** — in-game notifications for all actions
- **Loot scanner** — finds and ranks nearby container contents by value
- **Snapshot** — exports blueprint + saves game with matching timestamped names

## Requirements

- **Windows 10 or 11**
- **Skyrim SE or AE** — Steam 1.7.104 (SKSE64 2.3.1), 1.6.1130–1.6.1170, or SE 1.5.97
- **SKSE64** ([skse.silverlock.org](https://skse.silverlock.org/))
- **Address Library for SKSE** ([Nexus](https://www.nexusmods.com/skyrimspecialedition/mods/32444))
- **.NET 10 Runtime** ([download](https://dotnet.microsoft.com/download/dotnet/10.0))
- **Claude Desktop** ([claude.ai/desktop](https://claude.ai/desktop))

## Installation

1. Extract the `Data` folder into your Skyrim directory (or install via MO2/Vortex)
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
      "args": ["<SKYRIM_PATH>\\Data\\SKSE\\Plugins\\SkyLinkAI_Server\\SkyrimMCP.dll"]
    }
  }
}
```

Replace `<SKYRIM_PATH>` with your Skyrim installation path.

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

With 74 tools covering player state, inventory, quests, NPCs, world interaction, combat, economy, magic, events, and a Papyrus VM bridge to 3,000+ mod functions — if Skyrim can do it, Claude can probably help.

See **[USAGE.md](USAGE.md)** for the full tool reference and advanced features.

## Building from Source

```powershell
# Prerequisites: VS2022 C++ workload, CMake 3.21+, vcpkg, .NET 10 SDK

$env:VCPKG_ROOT = "C:\vcpkg"

# Clone CommonLibSSE-NG (one-time)
git clone --depth 1 --branch v16.3.0 https://github.com/alandtse/CommonLibVR.git SkyrimMCPPlugin/extern/CommonLibSSE-NG

# Build SKSE plugin
cd SkyrimMCPPlugin
cmake --preset default
cmake --build build --config Release

# Build MCP server
dotnet build ..\SkyrimMCP

# Or build release package
cd ..
.\build_release.ps1 -Version "1.1.0"
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

## License

- **SKSE plugin** (`SkyrimMCPPlugin/`, distributed as `SkyrimMCPPlugin.dll`): GPL-3.0-or-later. It statically links CommonLibSSE-NG, which is GPL-3.0-or-later with a modding exception, so the compiled plugin carries that licence.
- **MCP server** (`SkyrimMCP/`): MIT. It is a separate program that talks to the plugin over a named pipe.

Full texts: [`SkyrimMCPPlugin/LICENSE`](SkyrimMCPPlugin/LICENSE) and [`SkyrimMCP/LICENSE`](SkyrimMCP/LICENSE). Source for both is in this repository.

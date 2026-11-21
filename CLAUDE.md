# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Skyrim MCP Server — a Model Context Protocol server that lets AI assistants (Claude, OpenAI, etc.) interact with Skyrim SE/AE via an SKSE plugin. The SKSE plugin runs inside Skyrim's process and exposes game state/actions over a named pipe. The C# MCP server relays between Claude Desktop (stdio) and the plugin (pipe). **Windows only.**

## Architecture

```
Claude Desktop <--stdio--> SkyLinkLauncher <--stdio/MCP--> C# MCP Server <--named pipe--> SKSE plugin host <--C ABI--> logic DLL
                           (optional)                                                     (inside Skyrim)
```

Both halves hot-reload (1.2.1):
- **Launcher** (`SkyLinkLauncher/`, run as `dotnet SkyLinkLauncher.dll`): runs a temp copy of the server, restarts it when a new build lands in `SkyLinkAI_Server\`, replays the MCP `initialize` handshake and sends `tools/list_changed`. Claude Desktop stays open. Claude Desktop ignores `list_changed` and reads the tool list once at startup: behaviour changes apply on the next call, but new tools and changed descriptions need Claude Desktop quit (from the tray) and reopened. A new chat is not enough (seen 2026-11-15: a new chat still had build 2's tool list while build 3's server answered).
- **Plugin** is two DLLs. `SkyrimMCPPlugin.dll` (host) is loaded by SKSE and never unloads. `SkyLinkAI\SkyLinkLogic.dll` (logic: every command handler) is loaded by the host from a temp copy and swapped while the game runs: automatically on the next request after a newer DLL is deployed, or with the `reload_plugin` action / `ReloadPlugin` tool.
- **No .exe anywhere** (SmartScreen/AV): both C# projects set `UseAppHost=false`; `build_release.ps1` fails if one appears.

### SKSE Plugin (`SkyrimMCPPlugin/` — C++)
Host DLL (`SkyrimMCPPlugin.dll`; changes here need a game restart):
- **Main.cpp** — SKSE entry point; on kDataLoaded registers events, loads the logic DLL, starts the pipe
- **HostCore.h/cpp** — Answers `ping`, `poll_events`, `reload_plugin`; auto-reloads; forwards everything else to the logic DLL; runs logic tasks on the game thread and counts them
- **LogicLoader.h/cpp** — Loads/swaps the logic DLL (temp copies in `%TEMP%\SkyLinkAI`), drains pending work first, restores the previous build if a new one fails. No CommonLib, so `tests/ReloadHarness.cpp` can drive it outside Skyrim
- **HostApi.h** — The C ABI between the two DLLs (bump `SKYLINK_ABI_VERSION` on any change)
- **PipeServer.h/cpp** — Named pipe server (`\\.\pipe\SkyrimMCP`) on background thread
- **EventSystem.h/cpp** — 17 game event sinks with noise filtering (sinks must stay in the host: the game holds pointers to them)

Logic DLL (`SkyLinkAI\SkyLinkLogic.dll`; hot-reloadable):
- **LogicMain.cpp** — Exports `SkyLink_Init/HandleRequest/GetInfo/Shutdown/AbiVersion`; forwards logging to the host
- **Protocol.h/cpp** — Command registry (std::unordered_map) routing ~90 actions
- **Helpers.h** — Shared utilities: ParseFormId, ResolveActor, ActorValue resolution, console command execution, game safety checker, console output capture
- **PlayerQueries.h/cpp** — Player state: info, inventory, skills, perks, spells, shouts, appearance, blueprint, toggles (god mode, immortal), combat status
- **NPCManager.h/cpp** — NPC operations: nearby NPCs, actor info, followers, detection, factions, move/kill/crosshair targeting
- **InventoryManager.h/cpp** — Item operations: add, remove, equip, unequip
- **QuestManager.h/cpp** — Quest operations: info, stages, aliases, items, start/stop/complete
- **WorldManager.h/cpp** — World interaction: weather, time, cells, objects, doors, containers, merchants, bounties, collision, teleport
- **CombatAnalysis.h/cpp** — Combat: state, damage stats, threat assessment
- **UIManager.h/cpp** — Menu state queries
- **UtilityManager.h/cpp** — Notifications, form search, SKSE plugins, save/load, load order
- **PapyrusBridge.h/cpp** — Runtime .psc scanner, Papyrus VM call bridge, function catalog
- **TaskQueue.h/cpp** — Marshals pipe-thread calls to the game thread through the host (`HostApi::addTask`), never SKSE directly

**Rules for logic-DLL code** (break one and a reload crashes the game): no game event sinks, hooks, trampolines, Papyrus native registrations or SKSE listeners (they leave pointers into the unloaded DLL; put them in the host); no long-lived threads; game-thread work only through `TaskQueue`; any object the game keeps calling back (like `PapyrusCallback`) must be counted so `SkyLink_Shutdown` can wait for it; only plain C types across `HostApi.h`.

Uses CommonLibSSE-NG (alandtse fork `alandtse/CommonLibVR`, branch `ng`, tag v16.3.0 or newer, local shallow clone in `extern/`; required for Skyrim 1.7.99+ / Address Library format 5), nlohmann-json, spdlog, fmt. Statically linked (x64-windows-static).

### C# MCP Server (`SkyrimMCP/` — .NET 10)
- **Program.cs** — MCP server host, registers IPipeClient singleton, auto-discovers tools
- **IPipeClient.cs** — Interface for pipe communication (testability)
- **PipeClient.cs** — Named pipe client implementation
- **SkyrimOffsets.cs** — FormID database (30+ items, 20+ locations) for name→ID resolution
- **Tools/ToolBase.cs** — Base class with DeserializeResponse(), NotifyInGame(), PageResponse() helpers
- **Tools/PlayerTools.cs** — 15 tools: player info, skills, perks, spells, shouts, appearance, factions, blueprint, toggles
- **Tools/InventoryTools.cs** — 6 tools: inventory (paged), equipped, add/remove, bulk add
- **Tools/QuestTools.cs** — 4 tools: quest info, stages, aliases, items
- **Tools/NPCTools.cs** — 7 tools: nearby NPCs, NPC detail, inventory, followers, detection, crosshair, kill
- **Tools/WorldTools.cs** — 15 tools: weather, cell, objects, doors, containers, merchants, bounties, teleport, collision, loot scan
- **Tools/CombatTools.cs** — 3 tools: combat state, damage stats, threats
- **Tools/UtilityTools.cs** — 12 tools: console commands, notifications, search, load order, save/load, events, menu state, game safety
- **Tools/PapyrusTools.cs** — 4 tools: catalog, function lookup, VM call, rescan

## Build Commands

```powershell
# IMPORTANT: Set vcpkg root (VS Dev PowerShell uses its own bundled copy otherwise)
$env:VCPKG_ROOT = "C:\vcpkg"

# C++ plugin — repo lives in WSL; map it first (not persistent across reboots):
#   net use W: \\wsl.localhost\Ubuntu-24.04
# build_plugin.ps1 mirrors SkyrimMCPPlugin to C:\src\skylink-plugin, builds in C:\build\skylink-plugin,
# and copies the DLL back to SkyrimMCPPlugin\output. Building straight from W: recompiles all of
# CommonLib every time: MSBuild's file tracker records no reads from the WSL share.
cd W:\home\cory\personal\skyrim_mcp
.\build_plugin.ps1 -Deploy        # add -Configure after CMakeLists/vcpkg.json/CMakePresets.json changes

# -Deploy works with the game running: the logic DLL hot-reloads on the next call. The host DLL is
# only copied when it changed (and needs a game restart then).
.\build_plugin.ps1 -Test           # also build and run tests/ReloadHarness.cpp (load/swap stress test, not shipped)

# C# MCP server + launcher, to <Skyrim>\Data\SKSE\Plugins\SkyLinkAI_Server and SkyLinkAI_Launcher.
# With Claude Desktop pointed at SkyLinkLauncher.dll, no need to close Claude: the launcher restarts the server.
.\build_plugin.ps1 -ServerOnly     # or -Deploy -Server to build and deploy both halves

# From WSL (Claude Code sessions): build a second worktree (<repo>-build, detached at develop) so the
# main checkout can stay on a feature branch. Logs land in <repo>-build/.build-logs/.
#   scripts/deploy-from-wsl.sh [-r ref] [-p plugin only | -s server only] [-c configure] [log-name]

# Release build for Nexus
.\build_release.ps1 -Version "1.5.0"
```

**Requires:** Visual Studio 2022 (or Build Tools) C++ workload, CMake 3.21+, vcpkg at `C:\vcpkg`, .NET 10 SDK

## Protocol

JSON over named pipe `\\.\pipe\SkyrimMCP`, newline-delimited.

Request: `{"id":"uuid","action":"...","params":{}}`
Response: `{"id":"uuid","success":bool,"data":{},"error":"..."}`

## Key Technical Decisions

- **Console commands:** Use ConsoleUtilSSE-style `CompileAndRunImpl` with `RELOCATION_ID(21416, AE_CHECK(SKSE::RUNTIME_SSE_1_6_1130, 21890, 441582))` (never test `patch()` alone: 1.7.104 has patch 104). Credit: VersuchDrei/ConsoleUtilSSE (MIT License). CommonLibSSE-NG's built-in CompileAndRun crashes on Skyrim >= 1.6.1130.
- **NPC targeting:** Use dot-notation `refId.command` (e.g., `FE124B0E.kill`). A refId starting with a digit must be quoted (`"61033226".moveto player`), or the console reads it as a command name. The `prid` command does NOT persist between separate ExecuteConsoleCommand calls.
- **Thread safety:** All game API calls go through TaskQueue to run on the game thread. Only `ping`, `poll_events`, and Papyrus catalog operations are handled directly on the pipe thread.
- **Papyrus VM calls:** DispatchStaticCall must NOT go through TaskQueue (deadlock). Called from pipe thread, callback fires on game thread.
- **Papyrus form arguments:** a dispatched call is type-checked exactly (no implicit cast as a script call gets): give each object Variable the declared parameter type (`SetObject(obj, paramType.GetRawType())`), or a Form parameter rejects an Actor and the call returns None (BUG-066).
- **SEH crash protection:** `__try/__except` around CompileAndRun catches access violations safely.
- **Static linking:** `x64-windows-static` vcpkg triplet required — dynamic linking causes SKSE load failure (0x0000007E).
- **CommonLibSSE-NG includes:** Must come BEFORE any Windows API headers. Use `void*` for HANDLE in headers, include `<windows.h>` only in .cpp files after CommonLibSSE.
- **Async save:** Fire-and-forget from Protocol layer — BGSSaveLoadManager::Save() blocks game thread for 10+ seconds with heavy mod lists.
- **Actor polymorphism:** `Helpers::ResolveActor(refId)` returns player if empty, specific actor by FormID otherwise. Used by GetInventory, GetFactions, SetActorValue.
- **Game safety checker:** `Helpers::CheckGameSafety()` detects loading, kill moves, saves. Functions like ToggleImmortalMode use it for fallback strategies.

## Key API Patterns (CommonLibSSE-NG main branch)

- Actor values: `AsActorValueOwner()->GetActorValue()`, not `Actor::GetActorValue()`
- Base actor value: `GetBaseActorValue()`, not `GetPermanentActorValue()`
- Quest objectives: use `state` field, not `IsCompleted()`
- Quest aliases: use `GetVMTypeID() == BGSRefAlias::VMTYPEID` with `static_cast`, not `dynamic_cast`
- Plugin entry: `SKSEPluginInfo()` / `SKSEPluginLoad()` macros, not manual constinit
- Logging: needs `#include <spdlog/sinks/basic_file_sink.h>`
- Papyrus VM: `SkyrimVM::GetVMRuntimeData().impl`, not `vm->impl` (layout shifted in 1.7.99)
- `EnumSet` values: `.underlying()` for the raw integer, not `static_cast`
- HUD notification: `RE::SendHUDMessage::ShowHUDMessage`, not `RE::DebugNotification`
- Do not define `SKSE_SUPPORT_XBYAK` at all (the fork tests `defined`, so `=0` turns it on)
- Always grep actual headers in `extern/CommonLibSSE-NG/include/` when in doubt

## Project Tracking

- **`project_work_items.json`** — Single source of truth for all features, bugs, and status (72+ items, 12 phases)
- **`ROADMAP.md`** — Human-readable feature roadmap
- **`CHARACTER_BLUEPRINT_SPEC.json`** — Character export/import specification
- **`project_dashboard.html`** — Visual dashboard for project_work_items.json

## Git Workflow

- **`main`** — Release-ready code only (README + .gitignore currently)
- **`develop`** — Active development branch
- **Feature branches:** `feature/XXX-name` for new features
- **Bugfix branches:** `bugfix/XXX-description` for bug fixes
- **Refactor branches:** `refactor/description` for code restructuring
- All branches merge to develop via `--no-ff`

## Prerequisites for Running
- SKSE64 installed in Skyrim
- Address Library for SKSE (SE or AE version)
- SkyrimMCPPlugin.dll in `<Skyrim>/Data/SKSE/Plugins/`
- .NET 10 Runtime
- Claude Desktop config pointing to the C# MCP server

# 1.2.1 hot reload: in-game test plan

Branch `feature/hot-reload`. What was already tested before handing over:
- **Plugin reload harness** (`build_plugin.ps1 -Test`): loads the real `SkyLinkLogic.dll` outside Skyrim and swaps it. 40 of 40 checks passed, 4 runs in a row:
  - 75 reloads, 25 of them while 4 threads sent about 70,000 requests
  - a reload is refused while a game-thread task is queued, then backs off and retries
  - a half-written file and a broken build are rejected, and the old build keeps working
  - a missing file is reported, and one temp copy is left behind
- **Launcher simulation** (WSL, real server, fake plugin): 31 of 31 checks passed. Covered:
  - handshake replay and `tools/list_changed`
  - requests sent mid-restart wait their turn
  - calls already in flight finish before the restart
  - crash-loop guard, clean shutdown
- **Launcher on Windows:** handshake, all 89 tools listed, `check_connection` through it, restart on a redeploy, clean exit. No `.exe` in either publish folder.

## Results, 2026-11-15 (in game, Claude Desktop)

Sections 3 to 6 passed, after two fixes found during the run:
- **Startup crash at the main menu:** the logic DLL's `SKSE::Init` called `GetPluginHandle` after plugin load, which SKSE answers with a deliberate crash. The host now gives the logic DLL a copy of SKSE's interface table captured at load (`SkseInterfaceProxy`).
- **`toggle_collision`:** it set the player's reference collision flag, which doesn't stop the player's collision. It now runs the console's `tcl` with nothing selected (the player's no-clip) and reports the state from the game's reply.

Other findings:
- Reloads took 10–12 ms. `reload_plugin`, Papyrus calls and `toggle_collision` all worked after reloads.
- `check_connection` didn't trigger the automatic reload: the host answered it without the logic DLL. Changed: it now reloads first, and reports `loadingScreen`.
- **Claude Desktop does not re-read the tool list on `list_changed`** New behaviour applies on the next call; new tools and changed descriptions need Claude Desktop restarted (a new chat reuses the list read at startup, seen 2026-11-15).
- Reload during a loading screen: five `reload_plugin` calls in the middle of a save load all reloaded in 11–21 ms, with no crash or freeze. To make this easier to confirm next time, `reload_plugin` now returns `duringLoadingScreen`, and the plugin log records "Loading screen started/ended".
- `get_game_safety` timed out during that load instead of reporting `isLoading: true`, because its check runs on the busy game thread. The host now answers it during a loading screen.

Needs a game restart to test (host DLL): reload on `check_connection`, loading-screen tracking, and `get_game_safety` during a load.

## 1. Build and deploy (game closed, Claude closed)

```powershell
cd W:\home\cory\personal\skyrim_mcp
git checkout feature/hot-reload
.\build_plugin.ps1 -Deploy -Server -Configure
```

Expect these files under `Data\SKSE\Plugins`:
- `SkyrimMCPPlugin.dll`
- `SkyLinkAI\SkyLinkLogic.dll`
- `SkyLinkAI_Server\SkyrimMCP.dll` (the old `SkyrimMCP.exe` is removed)
- `SkyLinkAI_Launcher\SkyLinkLauncher.dll`

## 2. Point Claude Desktop at the launcher

In `%APPDATA%\Claude\claude_desktop_config.json`, change `mcpServers.skyrim.args` to:

```json
"args": ["L:\\SteamLibrary\\steamapps\\common\\Skyrim Special Edition\\Data\\SKSE\\Plugins\\SkyLinkAI_Launcher\\SkyLinkLauncher.dll"]
```

Keep `"command": "dotnet"`. To go back, point `args` at `SkyLinkAI_Server\SkyrimMCP.dll` again.

## 3. Start up

Launch Skyrim through SKSE, load a save, start Claude, then run these:

| Call | Expect |
|---|---|
| `check_connection` | `pluginVersion` 1.2.1, `logic.loaded` true, `logic.version` 1.2.1, `logic.reloads` 0, `launcherVersion` 1.2.1, `serverPath` under `%LOCALAPPDATA%\SkyLinkAI\server-shadow`, `serverSource` in `SkyLinkAI_Server` |
| `get_player_info`, `get_known_enchantments` | Normal answers (the logic DLL works) |
| `poll_events` | Normal (now answered by the host) |
| `call_papyrus_function` `Utility.GetCurrentGameTime` | Returns the game time (Papyrus callback now lives in the host) |
| `call_papyrus_function` `PO3_SKSEFunctions.GetKnownEnchantments` | "Function not found", no crash |
| `toggle_collision` twice | Off, then on again (now reads the real state) |

The plugin log (`SkyrimMCPPlugin.log`) should show "Logic DLL loaded: v1.2.1".

## 4. Plugin hot reload (game running)

1. **Make a visible change:** in `SkyrimMCPPlugin\src\Protocol.cpp`, change the `game_thread_ping` response to also return `{"hello", "reloaded"}`. Or just rebuild unchanged; the build time still changes.
2. **Deploy:** run `.\build_plugin.ps1 -Deploy` with the game still running. Expect "Deployed logic DLL" and "Host DLL unchanged; not copied".
3. **Next call reloads:** make any call, for example `check_connection` (with the 2026-11-15 host; before that, only calls that reach the logic DLL). Expect `logic.reloads` 1 and a new `logic.built`. The log shows "A newer logic DLL is on disk; reloading" and "Logic DLL reloaded in N ms".
4. **Manual reload:** `reload_plugin` should return `reloaded: true` and `reloads` 2.
5. **Still works after reload:** `get_player_info`, a Papyrus call, `toggle_collision`.
6. **Reload during a loading screen:**
   1. Start a fast travel.
   2. During the loading screen, call `reload_plugin`.
   3. Either it reloads, or it says "Reload postponed … will be retried". It must not crash or hang longer than about 10 s.
   4. `duringLoadingScreen: true` in the reply (or "Manual reload requested during a loading screen" in the log) confirms the call landed in time.

## 5. Server hot reload (Claude stays open)

1. **Make a visible change:** in a tool description in `SkyrimMCP\Tools\UtilityTools.cs`, add a word, for example to `reload_plugin`. Or just rebuild unchanged.
2. **Deploy:** run `.\build_plugin.ps1 -ServerOnly` with Claude still running. Expect "Server: …" plus "Launcher not updated (in use by Claude?)". That launcher warning is expected while Claude is running.
3. **Next call uses the new server:** make the next call. `check_connection` should show a new `serverBuilt` and a new `server-shadow\<pid>-<n>` path.
4. **Tool list refresh:** Claude Desktop does not show the new description in the open chat (tested 2026-11-15). A new chat was thought to show it; a later session (2026-11-15) showed it needs Claude Desktop restarted. Behaviour changes still apply immediately.

## 6. Host change still needs a restart

A change to `HostCore.cpp`, `Main.cpp`, `PipeServer.cpp`, `EventSystem.cpp` or `LogicLoader.cpp`, deployed with the game running, prints "Host DLL changed but is in use (game running): restart Skyrim, then deploy again." That's expected.

## If something breaks

- **Plugin log:** `C:\Users\cory\OneDrive\Documents\My Games\Skyrim Special Edition\SKSE\SkyrimMCPPlugin.log`
- **Launcher and server messages:** Claude Desktop's MCP log for `skyrim`. Launcher lines start with `[SkyLinkLauncher]`.
- **Rollback:**
  1. Point Claude back at `SkyLinkAI_Server\SkyrimMCP.dll`.
  2. Check out `develop`.
  3. Run `.\build_plugin.ps1 -Deploy -Server` with the game closed.
  4. Delete `Data\SKSE\Plugins\SkyLinkAI\` and `SkyLinkAI_Launcher\`.

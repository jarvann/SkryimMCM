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

Not tested yet, and needs the game: the host DLL inside Skyrim, a reload with the game running, Papyrus calls through the new host callback, and whether Claude Desktop re-reads the tool list on `list_changed`.

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
3. **Next call reloads:** make any call, for example `check_connection`. Expect `logic.reloads` 1 and a new `logic.built`. The log shows "A newer logic DLL is on disk; reloading" and "Logic DLL reloaded in N ms".
4. **Manual reload:** `reload_plugin` should return `reloaded: true` and `reloads` 2.
5. **Still works after reload:** `get_player_info`, a Papyrus call, `toggle_collision`.
6. **Reload during a loading screen:**
   1. Start a fast travel.
   2. During the loading screen, call `reload_plugin`.
   3. Either it reloads, or it says "Reload postponed … will be retried". It must not crash or hang longer than about 10 s.

## 5. Server hot reload (Claude stays open)

1. **Make a visible change:** in a tool description in `SkyrimMCP\Tools\UtilityTools.cs`, add a word, for example to `reload_plugin`. Or just rebuild unchanged.
2. **Deploy:** run `.\build_plugin.ps1 -ServerOnly` with Claude still running. Expect "Server: …" plus "Launcher not updated (in use by Claude?)". That launcher warning is expected while Claude is running.
3. **Next call uses the new server:** make the next call. `check_connection` should show a new `serverBuilt` and a new `server-shadow\<pid>-<n>` path.
4. **Tool list refresh:** check whether Claude shows the new description without a new chat. This is the one thing nobody has tested: whether Claude Desktop re-reads the tool list on `list_changed`. If it doesn't, behaviour changes still apply immediately, and only new or renamed tools need a new chat.

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

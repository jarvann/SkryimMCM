# In-Game AI Chat and Voice: Scope

Draft, 13 Nov 2026. Covers an in-game text chat that talks to whichever AI the player uses, then a voice add-on.

## 1. Who asked for it

| Who | When | Request |
|---|---|---|
| Akatsuki2534 | 21 May 2026 | Can't use Claude while Skyrim is in front; switching windows minimises the game |
| ShiroFozy | 19 Oct 2026 | "you can make a little window inside Skyrim? i dont have 2 screens" |
| snowolf33, Adminoir, skaterman888 | May–Jun 2026 | Grok, Gemini, a free client, Ollama: any AI, not only Claude |
| HarvetusNS, mlpvermin | Mar 2026 | Talking to it by voice ("Jarvis, pause the game…") |
| Fork author (approved) | Nov 2026 | An in-game UX platform built on Codex. It overlaps this work, so coordinate before building. |

## 2. The deciding constraint: which AI apps can be reached from outside

To relay a message typed in Skyrim, something has to hand the text to the AI and get the reply back. AI apps split into three groups:

| Group | Examples | Can a program send it a prompt? | Who runs the tools |
|---|---|---|---|
| **A. Chat desktop apps** | Claude Desktop, ChatGPT desktop, Cherry Studio, 5ire | **No.** They have no input API; the only way in is faking keystrokes. | The app, through the SkyLink MCP server |
| **B. Command-line AI tools** | Claude Code (`claude -p`), Codex (`codex exec`), Gemini CLI (`gemini -p`) | **Yes.** They have a non-interactive mode with JSON output and can resume a session. They sign in with the player's existing subscription. | The tool itself, through the SkyLink MCP server |
| **C. Direct API** | Anthropic, OpenAI, Gemini, OpenRouter (Grok, DeepSeek…), xAI, Ollama, LM Studio | **Yes**, over HTTP | **We do.** Our bridge runs the tool loop. |

**What this means:** "relay to the desktop client" works for group B, not group A. Claude Desktop and the ChatGPT app keep working as they do today, in a second window. The in-game chat runs through group B or C.

Two notes:
- **MCP sampling** (an MCP server asking its client for a reply) would be the clean route for group A. As far as I know, Claude Desktop doesn't support it. Recheck that before ruling it out for good.
- **One adapter covers most of group C.** Gemini, OpenRouter, xAI, Ollama and LM Studio all offer an OpenAI-compatible endpoint, so an "OpenAI-compatible" adapter plus a native Anthropic adapter covers nearly everything.

## 3. Architecture

```
 Skyrim process                              Outside the game
┌──────────────────────────────┐          ┌──────────────────────────────────────────┐
│ SkyrimMCPPlugin.dll          │          │ SkyLinkBridge.exe (new, C# .NET 10)       │
│  • chat input (console/UI)   │◄─chat───►│  • conversation + history per save       │
│  • reply display             │  pipe    │  • provider adapters:                     │
│  • hotkeys, MCM natives      │          │      OpenAI-compatible │ Anthropic        │
│  • starts/stops the bridge   │          │      Claude Code CLI   │ Codex CLI        │
│                              │◄─tool───►│  • tool loop (API providers only)        │
│  existing tool pipe          │  pipe    │  • key store (Windows DPAPI)             │
└──────────────────────────────┘          │  • voice: mic, STT, TTS (phase 3)        │
            ▲                              └──────────────────────────────────────────┘
            │ tool pipe (unchanged)
   Claude Desktop / other MCP apps (alt-tab mode keeps working)
```

Why a separate process instead of HTTP calls from the DLL:
- **No game stalls or crashes.** A slow or failing network call can't freeze or kill Skyrim.
- **Keys stay out of game memory and save files.**
- **Reuse.** The bridge reuses the existing C# tool code: the MCP C# SDK's client tools plug straight into `Microsoft.Extensions.AI` function calling.
- **Faster iteration.** Providers change without a 15-minute C++ build.

### What the plugin has to change
1. **More than one client on the tool pipe (INFRA-009 becomes a prerequisite).** `PipeServer::ServerLoop` handles one client at a time. While Claude Desktop is connected, the bridge would get "pipe busy", and the reverse. The fix is a thread per client. `poll_events` also needs a separate read position per client, so one client doesn't consume another's events.
2. **A chat pipe** (`\\.\pipe\SkyLinkChat`) that can push in both directions: player message out, reply text or "thinking…" in. The tool pipe is request/response only, so the game can't push to it.
3. **Starting the bridge.** Launch `SkyLinkBridge.exe` hidden on `kDataLoaded` if chat is enabled, and close it on exit. Players get chat without running anything themselves.

## 4. In-game chat UI options

| Option | Dependencies | Effort | UX |
|---|---|---|---|
| **1. Console `ai` command** (MVP) | None | Low | Type `ai where is the nearest blacksmith` in the console; the reply prints there and shows as a HUD notification. The plugin already locates the console's compile-and-run function, so it can catch lines that start with `ai `. |
| 2. UIExtensions text box on a hotkey | UIExtensions + an ESP | Low–medium | Pop-up text box; replies as a message box or notifications. No scrollback. |
| 3. ImGui window (SKSE Menu Framework, or our own D3D11 overlay) | SKSE Menu Framework, or our own hook (possible ENB/Community Shaders conflicts) | Medium | A real chat window with scrollback, and pasting works |
| 4. HTML window (PrismaUI) | PrismaUI | Medium | The best-looking option; chat UI written in HTML/JS |
| 5. Custom Scaleform (Flash) menu | None | High | Native look; needs Flash/AS2 tooling |

**Recommendation:** ship option 1 first (no dependencies; it answers ShiroFozy's request on day one), then option 3 or 4 as the proper window. Before choosing between 3 and 4, check that each supports Skyrim 1.7.104.

## 5. Configuration and connection test (MCM)

- **MCM Helper** (SkyUI + MCM Helper, an ESL-flagged ESP, a JSON menu definition) holds:
  - provider and model
  - chat and push-to-talk hotkeys
  - whether replies are spoken
  - which tools need confirmation
  - a **Test connection** button, which calls a Papyrus function in our plugin that asks the bridge
- **What Test connection checks for each provider:**
  - **API:** list models, or send a 1-token completion.
  - **Ollama:** `GET /api/tags`, and confirm the chosen model supports tool calling.
  - **Claude Code / Codex:** the program is on PATH and logged in (`codex login status`, or a 1-turn `claude -p`).
- **API keys don't go through the MCM.** Skyrim's text fields don't take pasted text, and the MCM saves to a plain INI file. Use `SkyLinkBridge.exe setup` (a small console or desktop prompt) to store keys encrypted with Windows DPAPI. The MCM only shows "key set / not set". With the ImGui window, pasting a key in-game becomes possible later.

## 6. Bridge behaviour that matters in-game

- **Tool profiles.** 74 tool definitions cost about 15–20k tokens on every turn and overwhelm small local models. Offer three profiles:
  - **Core:** about 15 tools (player, inventory, NPCs, quests, teleport, console).
  - **Full:** all 74 tools.
  - **Custom:** the player picks.
  - Small Ollama models get Core by default.
- **Confirmation for destructive tools:** kill, remove items, set quest stage, console commands, load game. The game shows "AI wants to: kill Lydia. Y/N". This answers skaterman888's concern about the AI running wild.
- **Starting context.** Each conversation starts with a short state summary (location, level, health, active quest), which saves several tool calls.
- **Cost display.** Show tokens per turn and per session. It answers the "burning tokens" criticism head-on.
- **History.** One conversation per save, trimmed to a token budget, stored next to the bridge (not in the save file).
- **Running while a menu is open.** The console and chat window pause the game. SKSE tasks still run, but some actions, such as teleporting, may need the menu closed first. This needs testing.
- **Subscription tools (group B).** Pre-approve only the SkyLink tools (`claude -p --allowedTools "mcp__skyrim__*"`, or the matching Codex approval setting), so the AI can't reach the player's files from inside the game. Expect 2–5 s of start-up per turn; `--resume` keeps the conversation going.

## 7. Voice (follow-on)

The ElevenLabs key isn't required. Speech-to-text has good free options; ElevenLabs is mainly worth it for the AI's spoken voice.

| Job | Free / local | Paid (key) |
|---|---|---|
| **Speech-to-text** | **Whisper running locally** (Whisper.net, whisper.cpp in C#). Mantella and CHIM already run Whisper alongside Skyrim. Also Windows' built-in speech recognition (less accurate). | OpenAI transcription, ElevenLabs Scribe, Deepgram |
| **Text-to-speech (replies)** | Windows SAPI; Piper (local, good quality) | ElevenLabs (best), OpenAI TTS |

- **Push-to-talk, not always listening.** The plugin catches the hotkey and tells the bridge to start or stop recording. The bridge records the mic with NAudio, transcribes, and sends the text through the same chat path.
- **Reuse talkback.** Your `claude-talkback-mcp` already handles SAPI, ElevenLabs and VoiceBox output. Port that code instead of writing it again.
- **GPU contention.** Whisper small or base on the CPU handles short commands in about 1 s. The default should be CPU, so it doesn't compete with Skyrim for the GPU.
- **Later:** voice-to-voice APIs (OpenAI Realtime, Gemini Live) skip the separate STT and TTS steps, but they only work with API keys.

## 8. Proposed first push

**Providers (4 adapters, 2 patterns):**
1. OpenAI-compatible: covers OpenAI, Gemini (free tier), OpenRouter (Grok etc.), xAI, Ollama, LM Studio
2. Anthropic native (prompt caching keeps the tool definitions cheap)
3. Claude Code CLI (Claude Pro/Max subscribers, no API key)
4. Codex CLI (ChatGPT subscribers, no API key). Check with the Codex fork author first to avoid duplicate work.

**UI:** the console `ai` command plus HUD notifications.
**Config:** MCM Helper menu with Test connection, plus `SkyLinkBridge.exe setup` for keys.
**Not in the first push:** the chat window, voice, and Gemini CLI. Gemini CLI follows the same pattern as the other two and can come later.

## 9. Effort

Human hours assume one experienced developer. My time and tokens assume I write the code. Your builds and in-game testing come on top.

| # | Work package | Human | Me |
|---|---|---|---|
| **Phase 1: text chat MVP** | | | |
| 1.1 | Multi-client tool pipe, per-client event position (C++) | 4–6 h | 20 min, ~100k |
| 1.2 | Chat pipe, console `ai` hook, reply printing, bridge start/stop (C++) | 10–14 h | 45 min, 250–300k |
| 1.3 | Bridge core: host, chat loop, MCP-client tools, history, tool profiles, confirmations, cost counter (C#) | 16–24 h | 1–1.5 h, 400–600k |
| 1.4 | Four provider adapters with connection tests | 12–16 h | 45–60 min, 250–350k |
| 1.5 | MCM Helper menu, ESP, Papyrus functions, DPAPI key setup | 10–14 h | 45 min, 250–350k |
| 1.6 | Packaging, README/Nexus docs, model guide | 4–6 h | 20 min, ~100k |
| | **Phase 1 total** | **56–80 h** | **~4–5 h, 1.35–1.8M tokens** |
| | Your part: 2–3 plugin builds, in-game test of each provider | 8–12 h | – |
| **Phase 2: chat window** | ImGui or PrismaUI window, scrollback, paste, streaming text | 16–30 h | 1–1.5 h, 400–600k |
| **Phase 3: voice** | Push-to-talk, mic capture, Whisper/Windows/cloud STT, SAPI/Piper/ElevenLabs TTS | 27–40 h | ~1.5 h, 500–700k |

Phase 1 is roughly two weekends of your time, mostly testing. It doesn't fit the same weekend as 1.2. Suggested order: release 1.2 (blueprint and fixes) first, then build chat as 1.3 or 2.0.

## 10. Open questions and checks before building

1. **Codex fork.** What is it building? If it already has an in-game window, our bridge and chat pipe could be the backend it uses instead of a competing feature.
2. **UI framework.** Do SKSE Menu Framework and PrismaUI support 1.7.104 (and older 1.6 builds)?
3. **CLI behaviour.** Check the current flags of the CLI tools: `claude -p` stream-json/`--resume`, `codex exec --json`/resume, and how each one pre-approves MCP tools.
4. **Bridge start-up.** Is it acceptable for the plugin to start a separate .exe? Some antivirus tools flag a game starting child processes. The fallback is the player starting the bridge.
5. **Chat on by default?** It should probably start off, with the player enabling it in the MCM, so 1.x users see no change.

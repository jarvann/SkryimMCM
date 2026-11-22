# SkyLink AI — Release Build Script
# Run from PowerShell with VS 2022 (or Build Tools) C++ workload installed
# Produces the release folder and SkyLinkAI-v<Version>.zip
#
# The zip is a FOMOD (INFRA-025): fomod\ (the installer), "00 Core" (required; what goes inside Data) and
# "10 NPC slots" (SkyLinkAI.esl, optional and selected by default). MO2 and Vortex run it. Docs go in
# SKSE\Plugins\SkyLinkAI\ so they don't land loose in the Data folder.

param(
    [string]$Version = "1.5.0",
    [string]$OutputDir = "release",
    [string]$VcpkgRoot = "C:\vcpkg"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Building SkyLink AI v$Version ===" -ForegroundColor Cyan

# Set vcpkg
$env:VCPKG_ROOT = $VcpkgRoot

# Clean output
if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
$Core = "$OutputDir\00 Core"
$Slots = "$OutputDir\10 NPC slots"
$Plugins = "$Core\SKSE\Plugins"
$Docs = "$Plugins\SkyLinkAI"
New-Item -ItemType Directory -Path "$Plugins\SkyLinkAI_Server" -Force | Out-Null
New-Item -ItemType Directory -Path "$Plugins\SkyLinkAI_Launcher" -Force | Out-Null
New-Item -ItemType Directory -Path "$Plugins\SkyLinkAI" -Force | Out-Null

# Build C++ plugin
Write-Host "`n--- Building SKSE Plugin (C++) ---" -ForegroundColor Yellow
# Builds from a local mirror of the sources (see build_plugin.ps1 for why)
& "$PSScriptRoot\build_plugin.ps1" -VcpkgRoot $VcpkgRoot

# Build C# MCP Server
Write-Host "`n--- Building MCP Server (C#) ---" -ForegroundColor Yellow
dotnet publish SkyrimMCP -c Release -o "$Plugins\SkyLinkAI_Server" --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "MCP server build failed" }
dotnet publish SkyLinkLauncher -c Release -o "$Plugins\SkyLinkAI_Launcher" --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed" }

# Copy SKSE plugin DLL
Write-Host "`n--- Packaging ---" -ForegroundColor Yellow
Copy-Item "SkyrimMCPPlugin\output\SkyrimMCPPlugin.dll" "$Plugins\" -Force
Copy-Item "SkyrimMCPPlugin\output\SkyLinkAI\SkyLinkLogic.dll" "$Plugins\SkyLinkAI\" -Force

# FOMOD: the installer files, and the optional slot plugin.
New-Item -ItemType Directory -Path "$OutputDir\fomod", $Slots -Force | Out-Null
Copy-Item "fomod\ModuleConfig.xml" "$OutputDir\fomod\" -Force
(Get-Content "fomod\info.xml" -Raw).Replace("@VERSION@", $Version) | Set-Content "$OutputDir\fomod\info.xml" -Encoding UTF8
Copy-Item "plugin\SkyLinkAI.esl" "$Slots\" -Force

# No .exe in the package: the server and launcher are run with "dotnet <dll>", and
# SmartScreen and antivirus tools treat unsigned .exe files with more suspicion.
$exes = Get-ChildItem "$OutputDir" -Recurse -Filter *.exe
if ($exes) { throw "Release contains .exe files: $($exes.FullName -join ', ')" }

# Create example config
$config = @{
    mcpServers = @{
        skyrim = @{
            command = "dotnet"
            args = @("<SKYRIM_PATH>\Data\SKSE\Plugins\SkyLinkAI_Launcher\SkyLinkLauncher.dll")
        }
    }
}
$config | ConvertTo-Json -Depth 4 | Set-Content "$Docs\claude_desktop_config_example.json"

# Create README
@"
# SkyLink AI v$Version

An MCP server that lets AI assistants (Claude, etc.) interact directly
with Skyrim SE/AE's runtime engine via an SKSE plugin.

## Requirements
- Skyrim SE or AE: Steam 1.7.104, 1.6.1130-1.6.1170, GOG 1.6.1179, or SE 1.5.97
- SKSE64 matching your game version (2.3.1 for 1.7.104) (https://skse.silverlock.org/)
- Address Library for SKSE, current All-in-One for your game version (https://www.nexusmods.com/skyrimspecialedition/mods/32444)
- .NET 10 Runtime (https://dotnet.microsoft.com/download/dotnet/10.0)
- Claude Desktop (https://claude.ai/desktop)
- RaceMenu (optional): needed for the face builder, presets, sculpting, body
  morphs and overlays. Hair colour, skin tone and head parts work without it.

## Installation
1. Install with Mod Organizer 2 or Vortex (the installer asks about the
   optional NPC slots; keep them unless you don't want a plugin). Without a
   mod manager: copy what's inside "00 Core" into your Skyrim Data folder, and
   SkyLinkAI.esl from "10 NPC slots" if you want the slots
2. Install .NET 10 Runtime if not already installed
3. Configure Claude Desktop (see below)

## Claude Desktop Configuration
Edit %APPDATA%\Claude\claude_desktop_config.json:

{
  "mcpServers": {
    "skyrim": {
      "command": "dotnet",
      "args": ["<SKYRIM_PATH>\\Data\\SKSE\\Plugins\\SkyLinkAI_Launcher\\SkyLinkLauncher.dll"]
    }
  }
}

Replace <SKYRIM_PATH> with your Skyrim installation path.

Mod Organizer 2: Claude Desktop runs outside MO2, so it can't see MO2's virtual
Data folder. Point "args" at the mod's own folder instead, for example
<MO2_PATH>\mods\SkyLinkAI\SKSE\Plugins\SkyLinkAI_Launcher\SkyLinkLauncher.dll

The launcher runs the MCP server (SkyLinkAI_Server\SkyrimMCP.dll) and restarts it
when a new version is installed, so updating SkyLink does not need a Claude restart.
Pointing "args" straight at SkyLinkAI_Server\SkyrimMCP.dll also works, as in 1.2.0
and earlier.

## Usage
1. Launch Skyrim via SKSE
2. Open Claude Desktop
3. Ask Claude to interact with your game!

## Features
- About 180 MCP tools for reading and changing game state
- Player stats, inventory, spells, perks, shouts, appearance
- Quests, NPCs, followers, weather, world objects, containers, teleport
- Combat analysis, event system, crash log
- Face builder (experimental, needs RaceMenu): captures, measurement against a
  photo, sliders, makeup, hair and brow colour, sculpt, a lit studio room
- Hair picker and body measurement (experimental)
- Character blueprint export (with quest stages done) and apply (items, gear,
  gold, skills, level, perks, spells, shouts); quest replay
- Save health: live script pile-ups by mod, save check and clean
  (cleaning needs ReSaver), old saves against a changed load order
- Mod building (new in 1.5): patch a mod's script as a small override mod
  (needs the free Creation Kit's compiler), write small plugins, check masters,
  install into Vortex or MO2 and sort with LOOT, plugin limits
- Crash and runaway care (new in 1.5): known crash patterns, Windows crash
  dumps, LOOT's notes on your plugins, cleaning with SSEEdit, stopping a
  runaway script loop for the session, restart cycle and new games
- Journal: what the quest log says, objectives as shown
- Teleports show the game's loading screen
- Safety rules: moves, captures and face edits refuse in menus, scenes,
  combat, furniture and similar states, with the reason
- Papyrus VM bridge and form search

## Credits
- ConsoleUtilSSE approach by VersuchDrei (MIT License)
- CommonLibSSE-NG (alandtse fork; originally by CharmedBaryon and Ryan McKenzie)
- SKSE by Ian Patterson, Stephen Abel, and Paul Shortman
- Face and pose models: Google MediaPipe (Apache-2.0), see SkyLinkAI_Server\Models\NOTICE.txt
- ONNX Runtime by Microsoft (MIT)
- Guidebook answers from UESP (CC BY-SA 2.5), fetched on demand
- YamlDotNet by Antoine Aubry and contributors (MIT), reads the player's LOOT masterlist
  (LOOT's masterlist is read from your own LOOT install, never shipped)
- Plugin cleaning runs your installed SSEEdit (xEdit team) as a separate program;
  script compiling runs your Creation Kit's Papyrus compiler
- Save reading follows ReSaver (FallrimTools by Mark Fairchild, Apache-2.0); cleaning
  runs an installed ReSaver's own library. See SkyLinkAI_Server\NOTICE-ReSaver.txt

## License
- SkyrimMCPPlugin.dll and SkyLinkAI\SkyLinkLogic.dll: GPL-3.0-or-later (statically link CommonLibSSE-NG)
- SkyLinkAI_Server (MCP server) and SkyLinkAI_Launcher: MIT
- Source: https://github.com/jarvann/SkryimMCM
"@ | Set-Content "$Docs\README.txt"

# Exactly the FOMOD's folders at the top, and every folder the installer names must exist with files in it.
$expected = @("fomod", "00 Core", "10 NPC slots")
$top = Get-ChildItem $OutputDir | Where-Object { $expected -notcontains $_.Name }
if ($top) { throw "Release has unexpected top-level items: $($top.Name -join ', ')" }
[xml]$module = Get-Content "$OutputDir\fomod\ModuleConfig.xml" -Raw
[xml]$null = Get-Content "$OutputDir\fomod\info.xml" -Raw
foreach ($folder in $module.SelectNodes("//folder")) {
    $path = Join-Path $OutputDir $folder.source
    if (-not (Test-Path $path) -or -not (Get-ChildItem $path -Recurse -File)) { throw "FOMOD folder '$($folder.source)' is missing or empty" }
}
$coreTop = Get-ChildItem $Core | Where-Object { $_.Name -ne "SKSE" }
if ($coreTop) { throw "Core has files outside SKSE\: $($coreTop.Name -join ', ')" }

# Zip with Windows' tar (bsdtar), which writes "/" separators. Compress-Archive in
# Windows PowerShell 5.1 writes "\", which some extractors turn into odd file names.
$zip = Join-Path $PSScriptRoot "SkyLinkAI-v$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
& "$env:SystemRoot\System32\tar.exe" -a -c -f $zip -C $OutputDir "fomod" "00 Core" "10 NPC slots"
if ($LASTEXITCODE -ne 0) { throw "Zip failed" }

Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host "Release folder: $OutputDir\ (FOMOD: fomod\, 00 Core\, 10 NPC slots\)"
Write-Host "  SKSE Plugin: $Plugins\SkyrimMCPPlugin.dll"
Write-Host "  Logic DLL:   $Plugins\SkyLinkAI\SkyLinkLogic.dll"
Write-Host "  MCP Server:  $Plugins\SkyLinkAI_Server\"
Write-Host "  Launcher:    $Plugins\SkyLinkAI_Launcher\"
Write-Host "  Docs:        $Docs\README.txt, claude_desktop_config_example.json"
Write-Host "Zip for Nexus and GitHub: $zip"

# SkyLink AI: incremental build of the SKSE plugin
#
# Why this exists: the repo lives on the WSL share (W:). MSBuild's file tracker
# records no reads from that drive, so every .cpp on W: looks never-compiled and
# all ~500 CommonLib files rebuild every time. This script mirrors the plugin
# sources to a local disk (robocopy copies only changed files and keeps their
# timestamps), builds there, and copies the DLL back to SkyrimMCPPlugin\output.
#
# Usage (from the repo root, e.g. W:\home\cory\personal\skyrim_mcp):
#   .\build_plugin.ps1            # mirror + build
#   .\build_plugin.ps1 -Deploy    # ...and copy the DLL into Skyrim
#   .\build_plugin.ps1 -Configure # force a CMake configure (after CMakeLists/vcpkg/preset changes)
#   .\build_plugin.ps1 -Server    # also publish the C# MCP server next to the plugin, in
#                                 # Data\SKSE\Plugins\SkyLinkAI_Server (the layout the release uses).
#                                 # Close the Claude app first: it locks SkyrimMCP.dll.
#   .\build_plugin.ps1 -ServerOnly # just the server (seconds), e.g. after a C#-only change

param(
    [switch]$Deploy,
    [switch]$Configure,
    [switch]$Server,
    [switch]$ServerOnly,
    [string]$VcpkgRoot = "C:\vcpkg",
    [string]$LocalSrc = "C:\src\skylink-plugin",
    [string]$BuildDir = "C:\build\skylink-plugin",
    [string]$SkyrimPlugins = "L:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data\SKSE\Plugins"
)

$ErrorActionPreference = "Stop"
$repoPlugin = Join-Path $PSScriptRoot "SkyrimMCPPlugin"
$env:VCPKG_ROOT = $VcpkgRoot

$serverDir = Join-Path $SkyrimPlugins "SkyLinkAI_Server"

function Publish-Server {
    Write-Host "--- Publishing MCP server to $serverDir ---" -ForegroundColor Yellow
    dotnet publish (Join-Path $PSScriptRoot "SkyrimMCP") -c Release -o $serverDir --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw "MCP server publish failed (is the Claude app still running? It locks SkyrimMCP.dll)" }
    Write-Host "Server: $serverDir\SkyrimMCP.dll" -ForegroundColor Green
}

if ($ServerOnly) { Publish-Server; return }

# 1. Mirror sources to the local disk. Generated folders are neither copied nor deleted.
#    The CommonLib clone contains Linux symlinks (AI-tool doc aliases such as .cody.md ->
#    .claude/CLAUDE.md) that Windows cannot open over the WSL share: /XJ skips links and
#    /XF names them, so robocopy doesn't fail on them. /R:1 /W:1 stops robocopy's default
#    of retrying a failed file a million times, 30 s apart.
Write-Host "--- Syncing sources to $LocalSrc ---" -ForegroundColor Yellow
robocopy $repoPlugin $LocalSrc /MIR /XJ /R:1 /W:1 `
    /XD build output .git .vs `
    /XF .cody.md .cursorrules COPILOT.md AGENTS.md `
    /NFL /NDL /NJH /NJS /NP | Out-Host
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

# 2. Configure only when needed. The first run on a new build tree is a full build;
#    later runs recompile only what changed.
if ($Configure -or -not (Test-Path (Join-Path $BuildDir "CMakeCache.txt"))) {
    Write-Host "--- Configuring ---" -ForegroundColor Yellow
    Push-Location $LocalSrc
    try {
        cmake --preset default -B $BuildDir
        if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
    } finally { Pop-Location }
}

# 3. Build.
Write-Host "--- Building ---" -ForegroundColor Yellow
cmake --build $BuildDir --config Release
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }

# 4. Copy the DLL back into the repo, where build_release.ps1 and the docs expect it.
$dll = Join-Path $LocalSrc "output\SkyrimMCPPlugin.dll"
New-Item -ItemType Directory -Path (Join-Path $repoPlugin "output") -Force | Out-Null
Copy-Item $dll (Join-Path $repoPlugin "output\") -Force
Write-Host "Built: $repoPlugin\output\SkyrimMCPPlugin.dll" -ForegroundColor Green

if ($Deploy) {
    Copy-Item $dll "$SkyrimPlugins\" -Force
    Write-Host "Deployed to $SkyrimPlugins" -ForegroundColor Green
}

if ($Server) { Publish-Server }

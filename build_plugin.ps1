# SkyLink AI: incremental build of the SKSE plugin
#
# Why this exists: the repo lives on the WSL share (W:). MSBuild's file tracker
# records no reads from that drive, so every .cpp on W: looks never-compiled and
# all ~500 CommonLib files rebuild every time. This script mirrors the plugin
# sources to a local disk (robocopy copies only changed files and keeps their
# timestamps), builds there, and copies the DLL back to SkyrimMCPPlugin\output.
#
# The plugin is two DLLs: SkyrimMCPPlugin.dll (the host, loaded by SKSE) and
# SkyLinkAI\SkyLinkLogic.dll (every command handler). The host reloads the logic DLL
# while the game runs, so after a logic-only change -Deploy is enough: the next call
# from the AI picks up the new build, no restart. A host change still needs a restart.
#
# Usage (from the repo root, e.g. W:\home\cory\personal\skyrim_mcp):
#   .\build_plugin.ps1            # mirror + build
#   .\build_plugin.ps1 -Deploy    # ...and copy the DLLs into Skyrim (works with the game running)
#   .\build_plugin.ps1 -Configure # force a CMake configure (after CMakeLists/vcpkg/preset changes)
#   .\build_plugin.ps1 -Server    # also publish the C# MCP server and its launcher next to the plugin:
#                                 # Data\SKSE\Plugins\SkyLinkAI_Server and SkyLinkAI_Launcher.
#                                 # When Claude runs the server through the launcher, the launcher
#                                 # restarts it on the new build; no need to close Claude.
#   .\build_plugin.ps1 -ServerOnly # just the server and launcher (seconds), e.g. after a C#-only change
#   .\build_plugin.ps1 -Test      # also build and run the reload test harness (not shipped)

param(
    [switch]$Deploy,
    [switch]$Configure,
    [switch]$Server,
    [switch]$ServerOnly,
    [switch]$Test,
    [string]$VcpkgRoot = "C:\vcpkg",
    [string]$LocalSrc = "C:\src\skylink-plugin",
    [string]$BuildDir = "C:\build\skylink-plugin",
    [string]$SkyrimPlugins = "L:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data\SKSE\Plugins"
)

$ErrorActionPreference = "Stop"
$repoPlugin = Join-Path $PSScriptRoot "SkyrimMCPPlugin"
$env:VCPKG_ROOT = $VcpkgRoot

$serverDir = Join-Path $SkyrimPlugins "SkyLinkAI_Server"
$launcherDir = Join-Path $SkyrimPlugins "SkyLinkAI_Launcher"

function Publish-Server {
    Write-Host "--- Publishing MCP server to $serverDir ---" -ForegroundColor Yellow
    dotnet publish (Join-Path $PSScriptRoot "SkyrimMCP") -c Release -o $serverDir --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw "MCP server publish failed (if Claude runs SkyrimMCP.dll directly rather than through the launcher, close Claude first: it locks the file)" }
    Write-Host "Server: $serverDir\SkyrimMCP.dll" -ForegroundColor Green
    # 1.2.0 and earlier also published SkyrimMCP.exe; nothing uses it now.
    Remove-Item (Join-Path $serverDir "SkyrimMCP.exe") -ErrorAction SilentlyContinue

    # The launcher only changes when its own code does. While Claude runs it, its DLL is
    # locked; skip it then rather than fail.
    Write-Host "--- Publishing launcher to $launcherDir ---" -ForegroundColor Yellow
    dotnet publish (Join-Path $PSScriptRoot "SkyLinkLauncher") -c Release -o $launcherDir --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Launcher not updated (in use by Claude?). The server was still published." -ForegroundColor DarkYellow
    } else {
        Write-Host "Launcher: $launcherDir\SkyLinkLauncher.dll" -ForegroundColor Green
    }
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
$testFlag = if ($Test) { "ON" } else { "OFF" }
$cache = Join-Path $BuildDir "CMakeCache.txt"
$needConfigure = $Configure -or -not (Test-Path $cache) -or
    -not (Select-String -Path $cache -Pattern "^SKYLINK_BUILD_TESTS:BOOL=$testFlag" -Quiet)
if ($needConfigure) {
    Write-Host "--- Configuring ---" -ForegroundColor Yellow
    Push-Location $LocalSrc
    try {
        cmake --preset default -B $BuildDir "-DSKYLINK_BUILD_TESTS=$testFlag"
        if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
    } finally { Pop-Location }
}

# 3. Build.
Write-Host "--- Building ---" -ForegroundColor Yellow
cmake --build $BuildDir --config Release
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }

# 4. Copy the DLLs back into the repo, where build_release.ps1 and the docs expect them.
$hostDll = Join-Path $LocalSrc "output\SkyrimMCPPlugin.dll"
$logicDll = Join-Path $LocalSrc "output\SkyLinkAI\SkyLinkLogic.dll"
New-Item -ItemType Directory -Path (Join-Path $repoPlugin "output\SkyLinkAI") -Force | Out-Null
Copy-Item $hostDll (Join-Path $repoPlugin "output\") -Force
Copy-Item $logicDll (Join-Path $repoPlugin "output\SkyLinkAI\") -Force
Write-Host "Built: $repoPlugin\output\SkyrimMCPPlugin.dll + SkyLinkAI\SkyLinkLogic.dll" -ForegroundColor Green

if ($Test) {
    Write-Host "--- Reload test harness ---" -ForegroundColor Yellow
    & (Join-Path $BuildDir "Release\SkyLinkReloadHarness.exe") $logicDll
    if ($LASTEXITCODE -ne 0) { throw "Reload test harness failed" }
}

if ($Deploy) {
    # The logic DLL can be replaced while the game runs: the host loaded a temp copy and
    # picks the new one up on the next call.
    $logicTarget = Join-Path $SkyrimPlugins "SkyLinkAI"
    New-Item -ItemType Directory -Path $logicTarget -Force | Out-Null
    Copy-Item $logicDll "$logicTarget\" -Force
    Write-Host "Deployed logic DLL to $logicTarget (hot-reloads on the next call if the game is running)" -ForegroundColor Green

    # The host DLL is locked while the game runs. Only copy it when it changed.
    $hostTarget = Join-Path $SkyrimPlugins "SkyrimMCPPlugin.dll"
    $hostChanged = -not (Test-Path $hostTarget) -or
        ((Get-FileHash $hostDll).Hash -ne (Get-FileHash $hostTarget).Hash)
    if ($hostChanged) {
        try {
            Copy-Item $hostDll "$SkyrimPlugins\" -Force
            Write-Host "Deployed host DLL to $SkyrimPlugins" -ForegroundColor Green
        } catch {
            Write-Host "Host DLL changed but is in use (game running): restart Skyrim, then deploy again." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "Host DLL unchanged; not copied" -ForegroundColor Green
    }
}

if ($Server) { Publish-Server }

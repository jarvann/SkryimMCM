#!/bin/bash
# Build and deploy SkyLink from WSL while the main checkout stays on a feature branch.
#
# Builds from a second worktree checked out (detached) at a ref, default develop, by calling
# build_plugin.ps1 through Windows PowerShell. Works with the game and Claude Desktop running:
# the logic DLL hot-reloads and the launcher restarts the server (see CLAUDE.md).
#
# Usage:  scripts/deploy-from-wsl.sh [options] [log-name]
#   -r REF      ref to build (default develop)
#   -p          plugin only (no C# server/launcher)
#   -s          server only (no plugin)
#   -c          also run CMake configure (after CMakeLists.txt / vcpkg.json changes)
#   log-name    log file name without .log (default build-<timestamp>)
#
# Environment (all optional):
#   SKYLINK_BUILD_WORKTREE  worktree to build in (default <repo>-build; created if missing)
#   SKYLINK_WSL_DRIVE       Windows drive mapped to the WSL root (default W:, `net use W: \\wsl.localhost\Ubuntu-24.04`)
#   SKYLINK_LOCAL_SRC       Windows mirror of the sources (default C:\src\skylink-hr)
#   SKYLINK_BUILD_DIR       Windows build folder (default C:\build\skylink-hr)
#
# Logs go to <worktree>/.build-logs/. The summary lines (errors, what was deployed) are printed at the end.
# Messages that are normal: "Host DLL changed but is in use" (only its build timestamp changed, unless
# you changed host code: then restart Skyrim and deploy again), and "Launcher not updated" (Claude Desktop
# has it open; the server was still published).
set -u

REF=develop
MODE="-Deploy -Server"
CONFIGURE=""
while getopts "r:psc" opt; do
    case $opt in
        r) REF=$OPTARG ;;
        p) MODE="-Deploy" ;;
        s) MODE="-ServerOnly" ;;
        c) CONFIGURE="-Configure" ;;
        *) sed -n '2,25p' "$0"; exit 2 ;;
    esac
done
shift $((OPTIND - 1))
NAME=${1:-build-$(date +%Y%m%d-%H%M%S)}

REPO=$(git -C "$(dirname "$(readlink -f "$0")")" rev-parse --show-toplevel) || exit 1
WORKTREE=${SKYLINK_BUILD_WORKTREE:-$REPO-build}
DRIVE=${SKYLINK_WSL_DRIVE:-W:}
LOCAL_SRC=${SKYLINK_LOCAL_SRC:-'C:\src\skylink-hr'}
BUILD_DIR=${SKYLINK_BUILD_DIR:-'C:\build\skylink-hr'}

if [ ! -d "$WORKTREE" ]; then
    echo "Creating build worktree $WORKTREE"
    git -C "$REPO" worktree add --detach "$WORKTREE" "$REF" || exit 1
fi
git -C "$WORKTREE" checkout -q --detach "$REF" || exit 1

# Git-ignored inputs the build needs: CommonLibSSE-NG and the strong-name key.
if [ ! -d "$WORKTREE/SkyrimMCPPlugin/extern/CommonLibSSE-NG" ]; then
    echo "Copying SkyrimMCPPlugin/extern into the worktree (once)"
    cp -r "$REPO/SkyrimMCPPlugin/extern" "$WORKTREE/SkyrimMCPPlugin/" || exit 1
fi
if [ -f "$REPO/SkyrimMCP/SkyrimMCP.snk" ]; then
    cp "$REPO/SkyrimMCP/SkyrimMCP.snk" "$WORKTREE/SkyrimMCP/"
else
    echo "Warning: SkyrimMCP/SkyrimMCP.snk is missing; the server build will fail (CS7027)"
fi

LOGS="$WORKTREE/.build-logs"
mkdir -p "$LOGS"
LOG="$LOGS/$NAME.log"
git -C "$WORKTREE" log --oneline -1 > "$LOG"

WIN_WORKTREE="$DRIVE${WORKTREE//\//\\}"
cd /mnt/c || exit 1
timeout 1500 powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
    "Set-Location '$WIN_WORKTREE'; .\\build_plugin.ps1 $MODE $CONFIGURE -LocalSrc '$LOCAL_SRC' -BuildDir '$BUILD_DIR'" >> "$LOG" 2>&1
STATUS=$?
echo "exit $STATUS" >> "$LOG"

echo "Log: $LOG"
head -1 "$LOG"
grep -E " error |error MSB|error C|Deployed|Host DLL|^Server:|Launcher not|^exit " "$LOG" | grep -v -E "MSB3027|MSB3021" | cut -c1-300
exit $STATUS

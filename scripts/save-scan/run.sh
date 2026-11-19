#!/usr/bin/env bash
# Usage: scripts/save-scan/run.sh <Windows path of a COPY of the .ess>   (read-only, but scan a copy anyway)
set -euo pipefail
RS_WIN=${RESAVER_DIR_WIN:-'C:\Games\Modding\Resaver'}
RS=${RESAVER_DIR:-/mnt/c/Games/Modding/Resaver}
DIR=$(dirname "$(readlink -f "$0")")
WIN_OUT=$(wslpath -w "$DIR/out")
CP="$WIN_OUT;$RS_WIN\\target\\ReSaver.jar;$RS_WIN\\target\\lib\\*"
"$RS/jre/bin/java.exe" -Xmx4g -cp "$CP" SaveScan "$1"

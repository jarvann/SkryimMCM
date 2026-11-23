#!/usr/bin/env bash
# Start the GOG game through MO2's global instance (SKSE), unless a Skyrim is already running, and wait for the main menu.
# Prints how long it took and the SKSE plugins that didn't load.  scripts/launch-gog.sh [timeout-seconds]
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
LIMIT=${1:-240}
LOG="/mnt/c/Users/cory/OneDrive/Documents/My Games/Skyrim Special Edition GOG/SKSE/skse64.log"
running() { powershell.exe -NoProfile -Command "Get-Process SkyrimSE -ErrorAction SilentlyContinue" | grep -q SkyrimSE; }
if running; then echo "A Skyrim is already running: not starting another"; exit 1; fi
start=$(date +%s)
(cd /mnt/c && powershell.exe -NoProfile -Command "Start-Process C:\Modding\MO2\ModOrganizer.exe '\"moshortcut://Skyrim Special Edition:Skyrim SKSE\"'")
for i in $(seq 1 $((LIMIT / 3))); do
  sleep 3
  if [ $i -gt 10 ] && [ $((i % 5)) = 0 ] && ! running; then echo "Skyrim isn't running after $(( $(date +%s) - start )) s (crashed, or MO2 is waiting on a dialog)"; break; fi
  r=$(bash "$HERE/pipe-call.sh" get_session_state '{}' 6000 </dev/null 2>/dev/null)
  if echo "$r" | grep -q '"mainMenuOpen":true'; then echo "main menu after $(( $(date +%s) - start )) s"; break; fi
done
[ -f "$LOG" ] && python3 - "$LOG" <<'PY'
import re, sys
t = open(sys.argv[1], errors='replace').read()
checked = re.findall(r'checking plugin (.+?\.dll)', t)
loaded = set(re.findall(r'plugin (.+?\.dll) \(.*?\) loaded correctly', t))
helpers = {'msdia140.dll'}   # Crash Logger's symbol reader: not a plugin
print(f"--- SKSE: {len(loaded)} plugins loaded")
for c in checked:
    if c not in loaded and c.lower() not in helpers:
        line = next((l for l in t.splitlines() if l.startswith('plugin ' + c)), 'no reason given')
        print('NOT LOADED:', line)
PY

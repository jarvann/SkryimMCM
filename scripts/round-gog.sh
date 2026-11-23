#!/usr/bin/env bash
# One GOG test round for 1.6 characters, from the running game: spawn a clone, a family across races and a follower, list
# requirements, export one, save, restart through MO2 and check faces, ranks and followers held. Writes photos to $OUT.
#   scripts/round-gog.sh <round-name>
set -u
HERE=$(cd "$(dirname "$0")" && pwd); cd "$HERE/.."
NAME=${1:-round}; OUT=${OUT:-/tmp/skylink-$NAME}; mkdir -p "$OUT"
pc() { bash scripts/pipe-call.sh "$@" </dev/null; }
mcp() { timeout 300 python3 scripts/mcp-call.py "$@" </dev/null 2>&1; }
json() { python3 -c "import sys,json; t=sys.stdin.read(); i=t.find('\n{'); print(json.dumps(json.loads(t[i:]) if i>=0 else {'raw':t[-400:]}))"; }
: > "$OUT/refs.txt"
spawn() { mcp spawn_npc "$2" > "$OUT/spawn-$1.txt"; r=$(grep -m1 '"refId"' "$OUT/spawn-$1.txt" | grep -o '[0-9A-F]\{8\}'); echo "-- $1 ${r:-FAILED}"; grep -E '"error"|problem|"slot":|raceName|templateName|"rank"|voiceHasFollower|"note": "(Seen|Their|Its)|averageMove|bodyPreset|standIns' "$OUT/spawn-$1.txt" | sed 's/^ */    /' | head -14; [ -n "$r" ] && echo "$1 $r" >> "$OUT/refs.txt"; sleep 2; }
check() { while read v r; do pc head_materials "{\"refId\":\"$r\"}" 15000 | python3 -c "
import json,sys
d=json.load(sys.stdin); e=d.get('error'); d=d.get('data') or {}
head=[s for s in d.get('shapes',[]) if s.get('feature')==4]
print('  $v',e or '', d.get('race'), d.get('voice'), 'tint:', (head[0].get('tint') or 'NONE (built head)')[-44:] if head else '-')"; done < "$OUT/refs.txt"; }
echo "=== spawning"; date +%T
spawn clone '{"name":"GOG Clone","face":"player","follower":false,"marryable":true,"attraction":"everyone","home":"here","dryRun":false}'
spawn sister '{"name":"GOG Sister","race":"BretonRace","family":"Sister","follower":true,"voice":"FemaleYoungEager","attraction":"women","home":"here","dryRun":false}'
spawn brother '{"name":"GOG Brother","race":"NordRace","family":"Brother","follower":false,"attraction":"mods decide","home":"here","dryRun":false}'
spawn cousin '{"name":"GOG Cousin","race":"ArgonianRace","family":"Cousin","female":false,"follower":false,"attraction":"no one","home":"here","dryRun":false}'
echo "=== now (new face files show after a restart under MO2)"; check
S=$(awk '/sister/{print $2}' "$OUT/refs.txt")
echo "=== requirements of the sister"; mcp character_requirements "{\"refId\":\"$S\"}" | json | python3 -c "
import json,sys; d=json.load(sys.stdin)
for k in ('required','optional'): print(' ',k,[(m.get('mod'),m.get('nexusId')) for m in d.get(k) or []])
print('  handed out',(d.get('handedOutByOtherMods') or {}).get('perksSpellsAndFactions'),'untraced',d.get('untraced'))"
echo "=== export of the sister"; mcp export_character "{\"refId\":\"$S\",\"modName\":\"GOG Sister Test\",\"follower\":true,\"family\":true,\"dryRun\":false}" | json | python3 -c "
import json,sys; d=json.load(sys.stdin); print(' ',d.get('error') or (d.get('zip'), d.get('masters'), d.get('placedAt',{}).get('what')))"
echo "=== save and restart"; mcp save_game "{\"saveName\":\"SkyLink_$NAME\"}" | grep -E 'success|error' | head -2; sleep 8
mcp restart_game '{"confirm":true,"saveFirst":false}' | grep -E 'launched|how|reason|error' | head -3
for i in $(seq 1 60); do r=$(pc get_session_state '{}' 6000 2>/dev/null); echo "$r" | grep -q '"mainMenuOpen":true' && break; sleep 3; done; echo "menu: $r" | cut -c1-90
mcp load_save "{\"saveName\":\"SkyLink_$NAME\"}" | grep -E 'loading"|error|reason'
for i in $(seq 1 40); do r=$(pc get_session_state '{}' 6000 2>/dev/null); echo "$r" | grep -q '"gameLoaded":true' && echo "$r" | grep -q '"loadingMenuOpen":false' && break; sleep 3; done; sleep 12
pc get_message_box '{}' 6000 | cut -c1-200; echo
echo "=== after the restart"; check
while read v r; do mcp get_factions "{\"refId\":\"$r\"}" | json | python3 -c "
import json,sys; d=json.load(sys.stdin); L=d.get('factions',[])
print('  $v',[(f.get('name') or f.get('formId'),f.get('rank')) for f in L if f.get('formId') in('0005C84D','0005C84E','00019809') or 'Family' in (f.get('name') or '')])"; done < "$OUT/refs.txt"
while read v r; do INTENSITY=0.7 bash scripts/npc-photo.sh "$r" "$OUT/photo-$v" 0 50 </dev/null >/dev/null 2>&1; done < "$OUT/refs.txt"; ls "$OUT"/photo-*/*.jpeg 2>/dev/null | wc -l
date +%T; echo finished

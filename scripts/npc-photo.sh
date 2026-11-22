#!/usr/bin/env bash
# Photograph an actor's face through SkyLink (studio light, free camera, screenshot), for checking spawned NPCs.
#   scripts/npc-photo.sh <refId> <out-folder> [offsetX] [distance]
# offsetX: first move the actor that many units east of the player (default 0: leave it where it is). Restores the camera and lights.
set -u
R=$1; OUT=$2; OFF=${3:-0}; DIST=${4:-55}
HERE=$(cd "$(dirname "$0")" && pwd)
pc() { bash "$HERE/pipe-call.sh" "$@"; }
tool() { timeout 90 python3 "$HERE/mcp-call.py" "$@"; }
if [ "$OFF" != 0 ]; then
  set -- $(tool get_player_info 2>/dev/null | grep '"pos[XY]"' | grep -o '[-0-9.]*' | head -2 | tr '\n' ' ')
  NX=$(python3 -c "print($1+$OFF)")
  for ax in "x $NX" "y $2"; do tool execute_console_command "{\"command\":\"\\\"$R\\\".setpos $ax\"}" >/dev/null 2>&1; done
  sleep 3
fi
tool studio_lights '{"preset":"neutral","intensity":1.0}' >/dev/null 2>&1
for i in 1 2 3 4 5 6 7 8 9 10; do e=$(pc face_camera '{"mode":"enter","hideHud":true}' 2>&1); echo "$e" | grep -q '"freeCamera":true' && break; sleep 0.6; done
sleep 1
pc face_camera "{\"mode\":\"aim\",\"refId\":\"$R\",\"angleDeg\":0,\"distance\":$DIST,\"heightOffset\":-3}" 2>&1 | grep -o '"aimed":[a-z]*\|"error":"[^"]*"'
sleep 2
SKYLINK_MCP_IMAGES="$OUT" tool take_screenshot '{"crop":"0.3,0,0.4,1","previewWidth":900}' 2>&1 | grep -o 'image saved[^]]*\|"error[^,]*'
pc face_camera '{"mode":"exit"}' >/dev/null 2>&1
tool studio_lights '{"preset":"off"}' >/dev/null 2>&1

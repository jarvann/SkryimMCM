#!/usr/bin/env python3
"""Build SkyLinkAI.esp (INFRA-024, light-flagged): blank NPC slots SkyLink fills from a blueprint, so spawned characters are saved
normally, and the Player Family faction (FEAT-304, plugin/family-ranks.json).

Runs SkyLink's own CreatePlugin through the deployed server (scripts/mcp-call.py), then copies the result to plugin/SkyLinkAI.esp,
which the release ships as an optional FOMOD file. Object IDs are fixed (slots from 0x800, the faction
0xA00, dialogue later from 0xA10) and must never change once released: saves refer to them.

    scripts/build-slot-plugin.py            # dry run: what would be written, and the round-trip check
    scripts/build-slot-plugin.py --write    # write plugin/SkyLinkAI.esp
"""
import json
import pathlib
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
SLOTS = 25  # of each race and sex
# The game uses an NPC's face file only while its race is the one its record was made with, so every playable race has its own slots.
# The same order as kSlotRaces in NPCManager.cpp.
RACES = [("Breton", 0x13741), ("Nord", 0x13746), ("Imperial", 0x13744), ("Redguard", 0x13748), ("High Elf", 0x13743),
         ("Dark Elf", 0x13742), ("Wood Elf", 0x13749), ("Orc", 0x13747), ("Khajiit", 0x13745), ("Argonian", 0x13740)]
BASE = {
    "class": "Skyrim.esm|0x0001326B",           # Citizen
    "packages": ["Skyrim.esm|0x000956B8"],      # DefaultSandboxCurrentLocation256: stays around where it stands
    "unique": True,
}
VOICE = {True: "Skyrim.esm|0x00013ADD", False: "Skyrim.esm|0x00013AD2"}  # EvenToned: both have follower lines


def records():
    out = []
    for race, race_id in RACES:
        for female in (True, False):
            for n in range(1, SLOTS + 1):
                sex = "F" if female else "M"
                out.append({"type": "NPC_", "objectId": f"{0x800 + len(out):X}", "editorId": f"SkyLinkSlot{race.replace(' ', '')}{sex}{n:02d}", "name": f"SkyLink {race} {sex}{n:02d}",
                            "race": f"Skyrim.esm|0x{race_id:08X}", "female": female, "voice": VOICE[female], **BASE})
    ranks = json.loads((REPO / "plugin" / "family-ranks.json").read_text())["ranks"]
    out.append({"type": "FACT", "objectId": "A00", "editorId": "SkyLinkPlayerFamily", "name": "Player Family",
                "ranks": [{"rank": r["rank"], **({"male": r["role"]} if r.get("sex") != "female" else {}),
                           **({"female": r["role"]} if r.get("sex") != "male" else {})} for r in ranks],
                "relations": [{"faction": "this", "reaction": 2}, {"faction": "Skyrim.esm|0x00000DB1", "reaction": 2}]})  # allies of each other and the player
    return out


def main(write):
    args = {"name": "SkyLinkAI.esp", "records": records(), "masters": ["Skyrim.esm"], "author": "SkyLink AI",
            "description": "Blank NPC slots SkyLink AI fills from a blueprint (optional).", "light": True, "dryRun": not write}
    run = subprocess.run([sys.executable, str(REPO / "scripts" / "mcp-call.py"), "create_plugin", json.dumps(args)],
                         capture_output=True, text=True, timeout=180)
    start = run.stdout.find("\n{")
    reply = json.loads(run.stdout[start:]) if start >= 0 else {"error": run.stdout[-500:] + run.stderr[-500:]}
    reply.pop("records", None)
    print(json.dumps(reply, indent=1)[:4000])
    if write:
        built = pathlib.Path("/mnt/c/Users/cory/OneDrive/Documents/My Games/Skyrim Special Edition/SkyLinkAI/Build/SkyLinkAI/SkyLinkAI.esp")
        target = REPO / "plugin" / "SkyLinkAI.esp"
        target.parent.mkdir(exist_ok=True)
        shutil.copyfile(built, target)
        print(f"copied to {target} ({target.stat().st_size} bytes)")


if __name__ == "__main__":
    main("--write" in sys.argv)

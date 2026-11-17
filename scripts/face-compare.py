#!/usr/bin/env python3
"""Compares two faces with the reference measurer (scripts/face-model-check.py): reference first.

  ~/.local/share/skylink-face-tools/venv/bin/python scripts/face-compare.py <reference> <capture> [tolerance%]
Prints each ratio with its gap and whether it is within tolerance, then the colour comparison.
"""
import importlib.util
import os
import sys

spec = importlib.util.spec_from_file_location("fm", os.path.join(os.path.dirname(os.path.abspath(__file__)), "face-model-check.py"))
fm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fm)

ref_path, cap_path = sys.argv[1], sys.argv[2]
tol = float(sys.argv[3]) if len(sys.argv) > 3 else 3.0
ref, _ = fm.measure(ref_path)
cap, _ = fm.measure(cap_path)
if not ref or not cap:
    sys.exit("no face found in " + (ref_path if not ref else cap_path))

print(f"pose  ref yaw {ref['pose']['yaw_deg']:+.1f} pitch {ref['pose']['pitch_deg']:+.1f} | capture yaw {cap['pose']['yaw_deg']:+.1f} pitch {cap['pose']['pitch_deg']:+.1f}")
for label, m in (("ref", ref), ("capture", cap)):
    if m["pose"]["flags"]:
        print(f"  {label} flags: {m['pose']['flags']}")
print(f"\n{'measurement':38s}{'reference':>10s}{'capture':>10s}{'gap':>9s}")
matched = 0
rows = list(ref["ratios"].items()) + list(ref["eyes"].items())
for k, rv in rows:
    cv = cap["ratios"].get(k, cap["eyes"].get(k))
    if "deg" in k:
        gap, ok = cv - rv, abs(cv - rv) <= 1.5
        g = f"{gap:+.1f}deg"
    else:
        gap = (cv - rv) / abs(rv) * 100
        ok = abs(gap) <= tol
        g = f"{gap:+.0f}%"
    matched += ok
    print(f"{k:38s}{rv:10.3f}{cv:10.3f}{g:>9s}  {'ok' if ok else '<<'}")
print(f"\n{matched}/{len(rows)} within {tol}% (tilt 1.5 deg)")
print(f"\n{'colour (relative to skin)':38s}{'reference':>10s}{'capture':>10s}{'diff':>9s}")
for k, rv in ref["colour"]["relative"].items():
    cv = cap["colour"]["relative"][k]
    print(f"{k:38s}{rv:10.1f}{cv:10.1f}{cv - rv:+9.1f}")
print("\nhex       reference   capture")
for k in ref["colour"]["hex"]:
    print(f"{k:9s} {ref['colour']['hex'][k]:>9s} {cap['colour']['hex'][k]:>9s}")
print(f"cast      a{ref['colour']['cast']['a']:+.0f} b{ref['colour']['cast']['b']:+.0f}   a{cap['colour']['cast']['a']:+.0f} b{cap['colour']['cast']['b']:+.0f}")

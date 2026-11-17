# Face builder: skill draft v1 (procedure and measurements)

Written 2026-11-17 by the test agent. This is the procedure another agent should follow to move a Skyrim character's face toward a reference image with the SkyLink face tools, plus the measurement method that was proven today. It builds on `face_management_notes_2026-11-16.md` and `face_builder_build_brief_2026-11-16.md`.

Status: draft. Steps 1 to 6 have been run end to end on one character. Step 7 (fitting by measurement) works for some features and not yet for others; see "What is proven".

## Tools the procedure uses

`GetFacePreset`, `SetFaceSliders`, `SetFaceTint`, `SetHeadPart`, `CaptureFace`, `DiffFacePresets`, `SaveRaceMenuPreset`, `LoadRaceMenuPreset`, `RepairFace`, plus a landmark measurement step that is not yet a tool (script: `face_measure.py`).

## Procedure

1. **Check the ground.** RaceMenu must be closed. Ask the player to sheathe weapons and spells first: `CaptureFace` resets the pose and has left readied spells stuck.
2. **Back up.** `SaveRaceMenuPreset("<folder>/<name>_start")`. Every later tool call also reports a `restoreWith` backup; record the first one.
3. **Read the face.** `GetFacePreset()`. Note three things before changing anything:
   - the sculpt summary: if a mesh has most of its vertices sculpted, sliders interact with the sculpt and small steps are mandatory;
   - tints with alpha above 0.5 around the eyes (they read as shape problems);
   - sliders at or beyond their menu limits (signs of two controls fighting).
4. **Capture a baseline.** `CaptureFace()` with defaults (front, distance 32, heightOffset -3). Use the same view and settings for every later capture. Take two and confirm they agree before trusting comparisons.
5. **Measure reference and baseline.** Run the landmark measurement on both images. It returns scale-free ratios (table below). Work from the differences, largest first, ignoring anything under the noise floor.
6. **Change in groups, smallest safe step, then capture and re-measure.** Order that worked:
   1. makeup tints (cheap, no geometry risk);
   2. eye opening and size;
   3. lower face (chin, jaw, mouth);
   4. head parts (brows, eyes, hair) last, since they change what the detector sees.
   Send sliders that affect different ratios together in one `SetFaceSliders` call to save round trips; never bundle two sliders that affect the same ratio until each one's direction is known.
7. **Keep or revert each change by the measurement and by eye.** If a ratio did not move, the slider has no effect on this head: revert it and record it as inert. If the capture looks worse although the ratio improved, trust the eye and revert.
8. **Save a named preset at each accepted stage** (`<name>_v2`, `_v3`). Never overwrite the start preset.
9. **Show the player** a capture and the ratio table, and ask before going further on anything that changes character (eye size, face shape).
10. **Finish:** final preset saved, list of test presets offered for deletion, player reminded to save the game.

## Measurement

Detector: MediaPipe Face Landmarker (478 points, model `face_landmarker.task`, float16). It found the face in every in-game capture tried (7 of 7) and in the reference render, including a 160x200 thumbnail upscaled to 900 px.

Ratios (landmark indices in brackets):

| Ratio | Definition |
|---|---|
| eye width / face width | mean of [33-133], [362-263] over [234-454] |
| eye opening / eye width | mean of [159-145], [386-374] over eye width |
| eye spacing / eye width | [133-362] over eye width |
| outer corner tilt (degrees) | angle of inner-to-outer corner line, mean of both eyes |
| brow to eye / eye width | mean of [105-159], [334-386] over eye width |
| nose length / face height | [168-2] over [10-152] |
| nose width / eye spacing | [129-358] over [133-362] |
| mouth width / face width | [61-291] over [234-454] |
| lip height / mouth width | [0-17] over [61-291] |
| jaw width / face width | [172-397] over [234-454] |
| chin height / face height | [17-152] over [10-152] |
| face width / face height | [234-454] over [10-152] |
| iris diameter / eye width | mean of [469-471], [474-476] over eye width |

### Noise floor

Three captures of the same unchanged face gave, per ratio, a spread of about 2 to 4 percent (eye opening 0.355 to 0.363, eye spacing 1.29 to 1.34, brow to eye 0.83 to 0.86). Treat differences under 5 percent as matched. The brow ratio is the least reliable: with faint brows or a fringe the detector guesses the brow line.

### First real comparison (Arya against the reference render)

| Ratio | Reference | Start | After pass | Start gap | Gap now |
|---|---|---|---|---|---|
| eye width / face width | 0.215 | 0.218 | 0.225 | +1% | +4% |
| eye opening / eye width | 0.426 | 0.355 | 0.384 | -17% | -10% |
| eye spacing / eye width | 1.239 | 1.292 | 1.208 | +4% | -3% |
| outer corner tilt | 8.1 | 8.9 | 8.9 | +0.8 | +0.8 |
| brow to eye / eye width | 0.707 | 0.829 | 0.806 | +17% | +14% |
| nose length / face height | 0.292 | 0.308 | 0.304 | +5% | +4% |
| mouth width / face width | 0.353 | 0.360 | 0.364 | +2% | +3% |
| lip height / mouth width | 0.388 | 0.472 | 0.480 | +22% | +24% |
| jaw width / face width | 0.769 | 0.760 | 0.757 | -1% | -2% |
| chin height / face height | 0.183 | 0.167 | 0.171 | -9% | -6% |
| face width / face height | 0.871 | 0.870 | 0.874 | 0% | 0% |

Reading: face shape, jaw, eye width, spacing, tilt, nose and mouth width already match within noise. The real gaps are eye opening, lip height and chin height (and brows, with low confidence).

## Slider responses measured so far (this head)

| Slider | Change | Measured effect |
|---|---|---|
| `NSK_EyelidOpen` | -0.3 to 0.15 (with `NSK_EyeSize` 0.26 to 0.45) | eye opening / eye width 0.355 to 0.384 |
| `NSK_EyelidOpen` | 0.15 to 0.6 | no further measured change; by eye, white starts to show above the iris. Stop near 0.3. |
| `EFM_Lip_Upper_Thickness` 0.31 to 0.1, `EFM_Lip_Lower_Thickness` 0.41 to 0.15 | | no measurable change in lip height. Reverted. Inert or too weak. |
| `EFM_Chin_Height` | -1.01 to -0.6 | chin height / face height 0.168 to 0.171 (weak, right direction). Kept. |
| Lip tint | navy 56% to rose 50% | visible colour change; not a geometry ratio |

From 2026-11-16, found by eye: `NSK_EyeAngle` (corner tilt), `CME_UpperEyesHeight` (upper eye area; hides the under-lid strip at -3.75), `EFM_Eyeball_Depth` (eyeball depth, the main fix for bulging), `EFM_Eyelid_Lower`, `CME_LowerEyesHeight`, `EFM_Eyes_AO_Depth` (no visible effect).

## What is proven, and what is not

Proven:
- Live tint and slider changes, capture, and preset save all work from the agent side with no player action, and leave skin colour and head parts intact.
- Captures are repeatable enough to compare (pose reset, head-aimed camera).
- Landmark detection works on game renders and gives usable proportions.
- The loop finds real gaps and confirms when a slider does nothing.

Not proven:
- Closing a gap by measurement alone. Eye opening stalled at -10 percent: the slider kept opening the lid by eye, but the measured ratio stopped moving, so either the detector under-reads wide lids or the eyeball, not the lid, sets the limit.
- Lip height: no slider found yet that moves it.
- Brow matching: detector unreliable there; brow colour follows hair colour for the packs tried.
- Anything beyond one character and one reference.

## What the MCP should codify

1. **`MeasureFace(image or "live")`**: run the landmark model server-side, return the ratio table. With two inputs, return differences and mark those under the noise floor as matched.
2. **`ProbeSlider(name, step)`**: set, capture, measure, restore, and return which ratios moved. Cache results per head and slider set. This replaces guessing directions and finds inert sliders automatically.
3. **`FitFace(reference, allowed)`**: the loop in steps 5 to 8, with small steps, a stop when improvement stalls, a preset saved per accepted step, and a final side-by-side.
4. **Guard in `CaptureFace`**: refuse or sheathe when weapons or spells are drawn.
5. **A zero threshold in `SetFaceSliders`' known-slider check** (a slider at 0 is currently reported as unknown).
6. **Brow tint**: a way to darken brows independent of hair colour (NiOverride node override on the brow shape), since brow colour was the one visible gap no tool could close.

## Files

- `face_measure.py`: the measurement script (Python, MediaPipe 0.10.32, OpenCV). Usage: `python face_measure.py ref=<image> live=<image> ...`; the first image is the reference.
- Presets: `<player folder>/Arya_Look_v2` (tints and eye step), tool backups under `_SkyLink/`.

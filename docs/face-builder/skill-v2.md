# Face builder skill, v2

Written 2026-11-17 by the test agent, after a full day of building and testing against SkyLink builds 29 to 57. It replaces the v1 draft. It is the procedure another agent should follow to move a Skyrim character's face toward a reference picture, or toward what the player describes, using the SkyLink face tools.

Scope of evidence: one player character (Arya Adahar, Breton female, High Poly Head, full-head sculpt), one rendered reference, and one control build from photographs of a real person on a neutral head. Treat slider values as examples and the rules as the skill.

## What the method can and cannot do

- **It can** match the measurable proportions of a face. On Arya, every proportion outside the eyes ended within 4% of the reference. On the control build, 10 of 17 proportions matched a photo after two passes from a neutral head.
- **It cannot, by measurement alone, produce a likeness.** The control build matched the numbers and still did not look like the person. Proportions capture bone structure. Likeness also needs soft tissue (cheek fullness, smile lines, lid shape) and colouring, and those are judged by eye.
- **It cannot move what no slider moves.** That needs the sculpt brush. Note that the lower eyelid was first judged unreachable by sliders on the strength of a measure later shown not to track it; see rule 11.
- **The player's taste outranks the reference.** The final Arya result came from the player's own notes, not from the reference numbers.

## Tools

| Purpose | Tools |
|---|---|
| Read | `GetFacePreset`, `GetHairTint`, `GetHairAndSkinColor`, `DiffFacePresets`, `SliderNotes` |
| Edit shape | `SetFaceSliders`, `ResetFace` |
| Edit colour | `SetFaceTint`, `SetBrowColor`, `SetHairColor` (with `exact: true`), `SetSkinTone` |
| Head parts | `ListHeadPartChoices`, `SetHeadPart`, `RemoveHeadPart` |
| See and measure | `CaptureFace`, `MeasureFace`, `SetFaceReference`, `ListFaceReferences` |
| Controlled light | `StudioGo`, `StudioLights`, `StudioStatus`, `StudioReturn` |
| Save and restore | `SaveRaceMenuPreset`, `LoadRaceMenuPreset`, `RepairFace`, `SaveGame` |

## Rules

These are the things that went wrong today when they were not followed.

1. **Measure only under the studio's neutral light.** Shape numbers are stable in any light, but lid measures and every colour measure shift with it. Blush, lips and skin were tuned in a dim tavern and in hazy daylight, and overshot badly.
2. **Compare before and after under the same light.** Judge each change by how the number moved, not by its absolute gap to the reference.
3. **Never assume a slider's direction.** Three guesses were wrong today. Change one slider, look (a side view for depth), then commit. Record what it did with `SliderNotes`.
4. **Re-measure everything after each change.** Ratios are coupled: thinning the lips raised the measured chin height by about 10% with no chin change. Fix lips before chin.
5. **Use three samples.** The head drifts about 3° in pitch between captures and vertical ratios move with it. `MeasureFace(samples: 3)` takes the median.
6. **Do not chase a number the layer cannot deliver.** A blush layer shaped as a band across the nose will look heavy long before it measures like flushed cheeks. Thin brows will never measure as dark as heavy ones. Stop when it looks right.
7. **Judge hair and skin colour in the light they will be seen in.** The same auburn read as light auburn in the studio, mid auburn in daylight and dark red-brown at dusk.
8. **Keep a named preset at every accepted stage, and a game save before anything risky.** After restoring, prove it with `DiffFacePresets("live", "<preset>")`: all sections should be empty.
9. **Ask before changing the player's own choices.** The player had chosen slim brows and grey eyes; the reference wanted heavy brows and hazel eyes. Those stayed.
10. **Say when you got something wrong.** An eye-spacing change went the wrong way and the player liked the result anyway; it was still reported and then corrected on request.

11. **Judge the lower eyelid by eye, in a close-up.** The lower-lid measures do not follow the real lid edge: a sculpt that visibly lowered the lid left both numbers unchanged. Sliders marked inert for the lower lid on that evidence may not be. The upper-lid measure did respond to real changes.
12. **Probe with three samples.** With two, head-pitch drift made an inert slider look as if it moved three vertical measures by about 4%.

## Procedure A: tune an existing face toward a reference

1. **Prepare.** RaceMenu closed. `SaveRaceMenuPreset("<folder>/<name>_start")` and `SaveGame("<name>_before_face_work")`.
2. **Read the face.** `GetFacePreset()`. Note the sculpt summary (a full-head sculpt makes sliders less predictable), any tints above 50% around the eyes, and `SliderNotes()` for what earlier sessions learned.
3. **Register the reference.** `SetFaceReference(<path>, name)`. Read its flags: a turned head, a smile, an open mouth, a colour cast or a monochrome image each remove some measures from use.
4. **Go to the studio.** `StudioGo()`, confirm the lights with `StudioLights("neutral")` if the reply says they are not on. If a studio call replies "moving", call `StudioStatus` until it finishes.
5. **Baseline.** `MeasureFace(samples: 3)`. List the gaps, largest first. Ignore anything within tolerance.
6. **Shape, in this order:** lips, then chin and jaw, then nose, then brows, then eyes last. Send sliders that affect different measures together in one `SetFaceSliders` call. After each call, `MeasureFace(samples: 2 or 3)`.
7. **Colour, after shape:** blush, lips, brows, skin. One layer at a time, capture after each, and look at the picture as well as the number.
8. **Checkpoint.** `SaveRaceMenuPreset` with a new version number whenever a pass is accepted.
9. **Show the player** a capture and the short table. Then take their notes and apply them by eye, with front and side captures.
10. **Finish.** `StudioReturn()` (then `StudioStatus` if needed), `DiffFacePresets` against the final preset, remind the player to save the game.

## Procedure B: build a new face from a picture

1. Save the current face as a named preset and make a game save.
2. `StudioGo()`, then `ResetFace()`. This clears sliders and sculpt and keeps hair, eyes, brows and makeup.
3. Register the picture. If several pictures of the same person exist, register each and compare their numbers. Agreement across pictures confirms the measures describe the face and not one photo. Use the most frontal, neutral, best-lit picture as the target and the others as a cross-check.
4. Tune the measures a neutral capture can be compared on: eye spacing, eye width, nose length and width, brow height, jaw width, face shape.
5. If the picture is smiling, compare smile to smile for mouth, lips, chin and eye opening: `MeasureFace(expression: "smile", strength: 0.6, mouthOpen: 0.3)` matched a broad open smile well. Strength 1 squints more than a real smile.
6. Set colouring by eye from a colour picture: `SetHairColor("<hex>", exact: true)`, `SetBrowColor`, eye part, skin.
7. Shape the soft tissue by eye: cheek fullness, jaw softness.
8. Save the build as its own preset. Restore the player's face with `LoadRaceMenuPreset`, re-apply the brow colour, return from the studio, and verify with `DiffFacePresets`.

Photos of a real person give a resemblance in proportions and colouring, not a portrait. Say so to the player before starting.

## Measurement reference

`MeasureFace` returns, and compares against the registered reference:

- **Proportions (13):** eye width, eye opening, eye spacing, outer corner tilt, brow height, nose length, nose width, mouth width, lip height, jaw width, chin height, face width to height, iris size.
- **Lids (4):** upper and lower lid gap against the iris radius, and upper lid height and lower lid depth against eye width. The second pair does not move with gaze and is the one to tune by. An upper lid gap above about 0.8 of the iris radius reads as a stare.
- **Pose and expression:** yaw, pitch, roll, smile and mouth-open flags. Flagged samples are retaken.
- **Colour:** brightness of brows, lips and iris against the skin; redness of lips and cheeks against the skin; skin and iris warmth against the white of the eye. Compare these only under the neutral studio preset.
- **Lighting:** brightness, side balance, clipping, contrast, with warnings.

Tolerances that held in practice: 3% for proportions in the studio; treat anything under 5% outside the studio as matched. In the studio, two runs a minute apart agreed to about 1%.

## What the sliders did

Positive and negative are as the tools report them. Directions are for these head meshes; check before relying on them elsewhere.

### On Arya's sculpted head

| Slider | Effect |
|---|---|
| `NSK_LipThickness` | Lip height. -0.01 to -1.0 took lip height / mouth width from 0.48 to 0.39. |
| `CME_ChinUpDown` | Chin height, strong. Positive lengthens. |
| `EFM_Chin_Height` | Chin height, weak. |
| `CME_NoseUpDown` | Nose length. Positive raises the tip and shortens the nose; shows more nostril. |
| `EFM_Nose_Width` | Nose width. Negative narrows. |
| `CME_NoseBackForward` | Nose projection. Negative pulls it in. |
| `EFM_Nose_Bridge_Depth` | Bridge curve. -0.6 gave a slight concave bridge. |
| `ECE_NoseBridgeWidth` | Bridge width. Lower is narrower. |
| `CME_BrowUpDown` | Brow height. Negative lowers. |
| `CME_MouthBackForward` | Mouth depth. Negative moves it back. |
| `NSK_MouthWidth` | Mouth width. Positive widens. |
| `CME_EyesInOut` | Eye spacing. Negative brings the eyes together. |
| `CME_EyesBackForward` | Eye socket depth. Positive is forward. |
| `EFM_Eyeball_Depth` | Eyeball depth. More negative is further back; the main fix for bulging eyes. |
| `NSK_EyelidOpen`, `EFM_Eyes_Upper_Height` | Upper lid. Both open the eye at the top. Small steps; too much shows white above the iris. |
| `CME_UpperEyesHeight` | Upper eye area; hides the under-lid strip at -3.75. |
| `NSK_EyeAngle` | Outer corner tilt. |
| `CME_JawUpDown` | Back of the jaw. Set to -0.4 at the player's request; effect not confirmed from captures. |

`EFM_Eyes_Size`: opens both lids and narrows eye spacing (probe 0.01 to 0.8: eye spacing -7%, upper lid gap +6%, eye width +4%).

Inert on this head (the lower-eye entries were judged by the unreliable lower-lid measure and should be rechecked by eye): `CME_NoseLength`, `EFM_Eyeground_Height`, `EFM_Eyes_Height`, `EFM_Iris_Width`, `EFM_Lip_Upper_Thickness`, `EFM_Lip_Lower_Thickness`, `EFM_Eyes_Lower_Height`, `EFM_Eyes_Lower_Shape`, `EFM_Eyelid_Lower`, `CME_LowerEyesHeight`, `SPG_ECEEyebagShape`, `EFM_Eyes_AO_Depth`, and the vanilla `browsUpDown` at -0.5.

### On the neutral head

| Slider | Effect |
|---|---|
| `CME_EyesInOut` | 0 to +0.9 took eye spacing / eye width from 1.14 to 1.34. Positive moves the eyes apart. |
| `CME_NoseUpDown` | +0.35 took nose length / face height from 0.310 to 0.298. |
| `CME_BrowUpDown` | -0.6 to -0.8 lowered the brows about 5 to 10%. |
| `EFM_Nose_Width`, `EFM_Nose_Wing_Width` | Positive widens the nose. |
| `NSK_MouthWidth`, `CME_MouthWidth` | Positive widens the mouth. |
| `NSK_LipThickness` | Negative thins the lips. |
| `CME_ChinUpDown` | Positive lengthens the chin. |
| `CME_CheeksBlow`, `EFM_Cheek_Width` | Fuller cheeks. Judged by eye. |
| `EFM_Jaw_Shape` | Softer jaw at +0.5. Judged by eye. |

## Colour facts

- **Hair renders at twice its stored colour.** Ask for the colour you want to see; `SetHairColor` halves it. `exact: true` writes any hex into RaceMenu's own colour form. Without it the tool picks the nearest existing colour, and load orders often have nothing near auburn or strawberry blonde.
- **Brows follow the hair colour unless set.** `SetBrowColor` stores a colour per character and re-applies it after edits. Every preset load wipes RaceMenu's own per-part overrides.
- **Makeup tints respond weakly and unevenly.** Lips at full strength still read as a soft rose. Blush went 0.10, 0.20, 0.29 at 65%, 80% and 100%.
- **The skin tint layer barely changes how the skin measures.** A pale character stays pale. If skin is changed, change the face layer and the body tone together.
- **What survives a save and load.** RaceMenu keeps its own copy of the player's makeup and hair colour and puts it back on every load and cell change. Edits made outside its menu were being undone until build 62, which calls RaceMenu's own save functions after every face edit. From build 62, makeup and hair persist. Brow colour persists only with build 63's game-restart part installed; before that it reverts to the hair colour on load until any face tool runs. After any change to how looks are stored, test it: save, load, then `DiffFacePresets("live", "<preset>")`.
- **Dirt and blood mods** put dirt on the face as magic effects, not overlays. Remove the effects before measuring colour.
- **Cold-weather breath** crosses the lower face outdoors and corrupts mouth and lid measures. The capture now hides it.

## Results

**Arya, against the reference (studio, neutral light, at v9):**

| Measure | Reference | Result | Gap |
|---|---|---|---|
| Lip height / mouth width | 0.394 | 0.396 | +1% |
| Chin height / face height | 0.182 | 0.178 | -2% |
| Nose length / face height | 0.292 | 0.284 | -3% |
| Mouth width / face width | 0.354 | 0.365 | +3% |
| Upper lid gap / iris radius | 0.770 | 0.779 | +1% |
| Upper lid height / eye width | 0.249 | 0.226 | -9% |
| Lower lid depth / eye width | 0.150 | 0.136 | -9% |
| Eye opening / eye width | 0.415 | 0.374 | -10% |

The player then gave six notes of their own (less pink around the eyes and nose, mouth back, nose longer and flatter with a concave and narrower bridge, eyes closer and a little more open, jaw back down, eye sockets forward). Those were applied by eye and accepted as `Arya_Look_v10`, then `Arya_Look_v11` after the eye-spacing correction. v11 is the result.

**Control build from photographs (pass 2, smile to smile):** nose length 0%, jaw width 0%, lip height 0%, eye spacing -1%, iris size +1%, brow height +2%, mouth width -3%, nose width +4%, chin height -11%. Pass 3 added exact strawberry blonde hair, lighter brows, fuller cheeks and a softer jaw by eye. Closer, and still not a likeness.

A useful check from that build: two photos of the same person taken about 24 years apart agreed on eye width (0.213, 0.212), eye spacing (1.307, 1.320), nose length (0.295, 0.294) and iris size (0.468, 0.472).

## Added late in the day

- **`ProbeSlider(slider, value)`** (build 58). Sets, measures, puts the slider back, reports what moved and writes a slider note. Use it before committing to any slider. It found `EFM_Eyes_Size` in one call: it opens both lids and narrows eye spacing.
- **`SculptFace(region, direction, amount, radius)`** (build 60, lashes from build 62). Moves the mesh where no slider reaches. Amounts are in eye widths. Steps under about 0.03 are invisible; 0.15 on the lower lids showed white under the iris. It moves lashes, the tear duct and the lid strip with the lid, and leaves the eyeballs. The lid edge stayed smooth. Undo by loading the preset.
- **Combined references** (build 56): `SetFaceReference(<picture>, name, morePictures: [...])` takes colour from the picture whose colour is usable and, from build 60, leaves each picture out of the measures its pose skews.

## Still missing

1. **A lid measure that follows the real lid edge.** Calibration captures at known sculpt steps exist for this.
2. **Soft-tissue shaping tested on a real build.** The sculpt brush exists; cheeks, smile lines and chin have not been tried with it.
3. **Hair picker.** Search or tag hairstyles; names say nothing about shape and there are hundreds.
4. **Profile measurement.** Nose and chin projection and forehead slope from a side capture.
5. **One reference from several pictures,** taking the median and dropping flagged measures (`morePictures` exists as of build 56; untested).
6. **One combined edit call** for sliders, tints and head parts, to cut reloads.
7. **Side-by-side sheet** of reference and capture with the table, for the player.
8. **Body capture and measurement** for full-figure references.

## Files and presets

- Presets, in `CharGen\Presets\<player folder>\`: `Arya_Look_v11` (result), `Arya_Look_v10`, `Arya_Look_v9`, `Arya_Look_v4` (earliest restore point), `Control_Photo_pass1` to `pass3`.
- Save: `Arya_Look_v10_before_control_builds`.
- References on the PC, in `_Claude\face refs\`: `control_photo.jpg`, `control_photo_95_colour.jpg`, `control_illustration.png`. The Arya reference is `Downloads\Arya_reference_model.png` and `_Claude\Arya_reference_model.png`.
- Registered references: `default` (Arya render), `photo`, `photo95`.
- Earlier documents: `face_management_notes_2026-11-16.md` (file formats, mesh facts, sculpt findings) and `face_builder_build_brief_2026-11-16.md`.

## Dev notes

- Build 66 adds `lower_lid_edge` and `upper_lid_edge` to `MeasureFace` (FEAT-250): lid edges found in the picture itself, calibrated on the sculpt steps above. They follow a sculpted lower lid where the landmark lid measures did not; the left lower lid under-reads, so check it by eye.
- Brow colour is restored after loads and cell changes by the host DLL from build 63 (deployed with the game closed).

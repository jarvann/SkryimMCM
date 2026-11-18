# SkyLink AI v1.3.0

The face builder release. The AI can now look at your character, compare her with a picture, and edit her face until the
two match: sliders, makeup, hair, brows and sculpt. Alongside it: a hair picker, body measurement, quest progress in the
character blueprint, and safety rules that every tool applies itself. The face, hair and body tools are **experimental**.

## Face builder (experimental, needs RaceMenu)

- **See the face.** `CaptureFace` takes a repeatable portrait (front, angle or profile) with a free camera, waits out
  blinks and walking, and hides the HUD, breath and light effects. Expressions (smile, open mouth) for comparing with
  smiling photos.
- **Measure it.** `MeasureFace` reads facial landmarks with Google's MediaPipe models, run locally (no internet, no
  account), and turns them into proportions (eyes, lids, brows, nose, mouth, jaw) plus skin, lip, eye, brow and hair colour
  measured independently of the lighting. Black-and-white and sepia pictures are detected. `MeasureProfile` adds depth
  (nose projection, chin, brow ridge) from the head mesh.
- **Compare with a picture.** `SetFaceReference` registers a photo, drawing or several pictures of one face;
  `MeasureFace(reference)` lists every gap with a tolerance, and `FaceSideBySide` shows the two together.
- **Edit it.** `SetFace` applies sliders, makeup tints and head parts in one preset load (with a dry run), plus
  `SetFaceSliders`, `SetFaceTint`, `SetBrowColor`, `SetHairColor` (any colour, exact), `ResetFace` and `RepairFace`.
- **Learn what sliders do.** `ProbeSlider` and `ProbeSliders` move a slider and measure the change on this head.
- **Sculpt.** `SculptFace` moves regions of the face mesh (lids, cheeks, jaw, chin and more) through RaceMenu's sculpt
  data, aimed by the landmarks.
- **Studio.** `StudioGo` takes the player to a quiet room with fixed lights for repeatable captures; `StudioReturn` brings
  her back to the exact spot and facing.
- **Makeup that stays.** Tints, hair colour and brow colour set by SkyLink survive saves, loads, cell changes and restarts.

## Hair and body (experimental)

- **Hair picker.** `FindHairstyles` measures every hair mesh the character menu offers (length, fringe, volume behind the
  head, side sweep) and returns contact sheets of front and side silhouettes, so a style can be chosen without cycling
  through hundreds of heavy meshes in game.
- **Body.** `MeasureBody` and `SetBodyReference` measure shoulder, hip, torso and leg proportions. Reliable in the studio
  (repeats within 1-2%); refuses below a confidence floor.
- **Body morphs and overlays.** RaceMenu body morphs read and set correctly (each morph's value per key and the total), with
  case-sensitive name checks. Bodies managed by OBody are recognised: read them, change them through OBody.

## Character blueprint

- Exports now include **quest progress**: quests completed or started, with stage, type and the objectives seen (completed
  or failed). `notCaptured` lists what a blueprint can't hold.
- `ApplyBlueprint` (dry run first) adds missing items and gold and equips the blueprint's gear, adding anything it needs to
  equip. With `sections: "quests"` it lists the quests the other character finished or started that this one hasn't, with
  stages and objectives; it never changes quests.
- Form IDs carry their plugin name, so a blueprint works across load-order changes.

## Safety

Each kind of action has its own rule, applied by the tool, and `GetGameSafety(action)` answers the same way in advance:

- **Moving the player** (Teleport, studio trips) is refused during menus (including ones a mod keeps from pausing, and mods'
  own menus), combat, dialogue, scripted and OStim scenes, script-held controls, furniture, mounts, ragdoll, falling,
  bleeding out or carrying an object.
- **Captures and measurements** are refused in scenes, menus, combat or furniture; `TakeScreenshot` always works.
- **Face edits** wait for scenes, a mod's menu or a message box, and beast form.
- Changing head parts on NPCs with pre-built faces (most followers) is refused: the face breaks after a restart.
- Every game-thread task runs inside a fault guard (`TestCrashProtection` demonstrates it).

## Other additions and changes

- **Teleport** waits out the loading screen and reports where she landed, lists followers left behind (`MoveActorTo`
  brings them), knows the player homes by name, and `Teleport("back")` returns to the starting spot.
- **Volume:** own and effective values, every category at once, snapshot and restore.
- **Skyrim guidebook:** help answers are looked up on UESP first.
- **Crash log:** `GetCrashLog` reads Crash Logger reports and SkyLink's own recorder of what happened just before a crash.
- **`SetQuestStage`** sets a stage and reports whether the game applied it.
- **`GetAppearance(refId)`** reads an NPC's appearance; `TakeScreenshot` returns a small preview.
- **Papyrus calls** with form arguments work (they returned nothing before).
- Game text with accented characters no longer breaks a reply.

## Fixes

About 70 fixes from in-game testing since 1.2.1 (BUG-013 to BUG-085 in `project_work_items.json`).

## Known limits

- Likeness from a photo: proportions match closely, but a recognisable face also depends on soft tissue and hairstyle.
- Body silhouette widths and arm length are reported, not compared.
- A playing music track takes up to a minute to pick up a new Music volume.
- An outbound teleport shows a frozen frame instead of a loading screen.
- A normal quit is inferred from the absence of a crash report (this game version ends without a normal exit).
- Applying skills, perks, spells, level and quest progress from a blueprint is planned for 1.4.

## Not yet tested

- Hair meshes packed inside BSA archives (none on the test setup).
- GOG Skyrim (moved to 1.4).

## Requirements

Unchanged from 1.2.1, plus **RaceMenu** for the face builder, presets, sculpt, body morphs and overlays. The download is
larger (about 18 MB of models plus ONNX Runtime).

## Licences

- `SkyrimMCPPlugin.dll` and `SkyLinkLogic.dll`: GPL-3.0-or-later (they statically link CommonLibSSE-NG).
- MCP server and launcher: MIT.
- Face and pose models: Google MediaPipe, Apache-2.0, converted to ONNX unchanged (see `Models\NOTICE.txt`).
- ONNX Runtime: MIT.

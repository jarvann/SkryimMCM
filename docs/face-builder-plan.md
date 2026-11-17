# Face builder: plan

Status: planned for 1.4 (tracker phase 16, FEAT-228 to FEAT-236). Written 2026-11-16 from the in-game agent's build brief and face notes (`<Skyrim>\_Claude\face_builder_build_brief_2026-11-16.md`, `face_management_notes_2026-11-16.md`) and the player's answers below.

## Goal

Given a reference, the AI builds a face that matches it, working offline as far as possible: it reconstructs the face model outside the game, compares it with the reference, and iterates there. It then either writes a preset that loads and is right first time, or sets the in-game sliders until the live face matches. The player judges the result; the tools make sure only good candidates reach them.

## Decisions (player, 2026-11-16)

| Question | Answer |
|---|---|
| Reference kinds | All: another in-game character, a rendered image, a photo |
| What may change | Everything on the face: shape sliders, head parts, makeup tints, overlays and tattoos (full edit) |
| Hide hair for captures | Yes, as an option |
| Hair selection | Never cycle through hairs in game (some are heavy to load). Read the hair list, find pictures online (Nexus), choose, or offer a shortlist and toggle only between those |

## What is on disk (checked 2026-11-16)

- **Base meshes and vanilla morphs:** chargen TRI files (`FRTRI003`), e.g. `meshes\actors\character\character assets\FemaleHeadCharGen.tri`. Morph names are readable: `NoseUp`, `EyesMoveIn`, `NoseType1..30`, `EyesType1..28`, and so on. Format notes are in the in-game agent's face notes, section 2.
- **RaceMenu extended sliders:** `meshes\actors\character\FaceGenMorphs\<plugin>\`
  - `morphs.ini` adds an extension TRI to a chargen TRI (`extension = ...FemaleHeadCharGen.tri, EFM\Female\FemaleHead.tri`), with the files under `FaceGenMorphs\Morphs\<folder>`.
  - `races.ini` maps races to slider files.
  - `sliders\human.ini` defines each slider as two morphs: `EFM_Lip_Width = 128, Slider, EFM_Lip_Narrow, EFM_Lip_Wide`.
  - Some slider sets (`CME_*`, `NSK_*` were not found loose) are probably packed in BSA archives, so a BSA reader is needed.
- **Presets:** `.jslot` JSON in `SKSE\Plugins\CharGen\Presets` and `Exported`, holding sliders, tints, head parts and sculpt offsets per TRI host.
- **Exported heads:** `SKSE\Plugins\CharGen\<name>.nif` with a matching `.dds` (RaceMenu's head export). This is the exact live face geometry, useful as ground truth for checking the offline model.
- **Mod sources:** the install is Vortex-managed. `Data\vortex.deployment.json` maps every file to its source mod folder, and the folder names carry the Nexus mod ID (`KS Hairdos SSE-6817-...` is mod 6817). So a hair head part leads through its plugin to the Nexus page with its pictures.

## Design

Most of the work runs in the C# server. The plugin only reads and applies live state.

```
reference (NPC | render | photo)
        |
        v
  landmarks + ratios  <---- compare ---->  landmarks + ratios
                                                ^
                                                |
        offline face model: TRI base + vanilla morphs + extension morphs
        (slider ini) + sculpt + head parts + tint composite -> software render
                                                ^
                                                |
                       optimiser adjusts sliders / parts / tints
                                                |
                       +------------------------+------------------------+
                       v                                                 v
           write preset (.jslot + tint .dds)              set live sliders and tints
           load once, capture, verify                      capture, verify, fine-tune
```

- **In-game reference:** read the NPC's face values (morphs, presets, head parts, tints) and copy or fit them directly. Check for a FaceGen override first: some NPCs ship a pre-built face mesh, so their record values don't describe what's on screen. Use that mesh as the geometry reference instead.
- **Image or photo reference:** use landmarks only. Expect matching proportions, not a photographic likeness.
- **Offline first:** the optimiser runs against the offline model, which is fast and needs no game loads. The game is touched once to apply and once to verify. A small live fine-tune loop follows only if the verify capture disagrees with the model.
- **Hair:** pick from data, not by cycling. List hair parts grouped by source mod with their Nexus IDs; the AI looks up pictures and proposes a shortlist. Applying one is a single SetHeadPart call, and toggling only moves between shortlisted parts, on request, one at a time.

## Milestones

Estimates follow the project convention: human developer hours, then Claude session time and tokens.

| # | Item | Content | Human | Claude |
|---|---|---|---|---|
| M1 | FEAT-228 | Live control: GetFacePreset (live and from a `.jslot`), DiffFacePresets, SetFaceSliders, SetFaceTint, guard rails (RaceMenu open, backup first, tint texture) | 16-24 h | 2-3 h, ~2M |
| M2 | FEAT-229 | CaptureFace: fixed pose and camera, idle paused, head tracking off, optional hide hair, cropped preview (builds on FEAT-234) | 12-20 h | 2 h, ~1.5M |
| M3 | FEAT-235 | Offline face model: TRI, slider ini, BSA, `.jslot` and DDS readers; mesh reconstruction (base + morphs + extensions + sculpt); tint composite; software renderer; check against RaceMenu's head export | 60-90 h | 8-12 h, 6-9M |
| M4 | FEAT-230 | Measurement: landmark detector (ONNX in the server; MediaPipe Face Mesh to be validated on game renders), ratio table, CompareFace | 24-40 h | 4-6 h, 3-4M |
| M5 | FEAT-231/232 | Response map and optimiser on the offline model; preset writer (with tint texture); live fine-tune loop; candidate sheet (before, after, reference); agent skill | 40-60 h | 6-8 h, 4-6M |
| M6 | FEAT-236 | Hair picker: hair list by source mod with Nexus IDs, shortlist, toggle between shortlisted parts | 8-12 h | 1-2 h, ~1M |
| M7 | FEAT-233 | Sculpt-aware edits: eye pieces by connected component, clearance checks, sculpt fitting | 30-50 h | 5-8 h, 3-5M |

Total: about 190-300 human hours, or 28-41 hours of Claude session time and 20-30M tokens, plus the player's time testing in game. M1, M2 and M6 are independent and can ship first. M3 is the critical path for working offline.

## Unknowns

1. **Live extended sliders:** whether skee exposes a Papyrus call to set a named face slider and refresh. Ask the in-game agent to run GetScriptFunctions on NiOverride and CharGen. If there is none, fall back to writing an Exported preset and loading it.
2. **Landmarks on game renders:** may fail on stylised heads, fringes or heavy makeup. Test early in M4 on captures of the player's own character.
3. **Tint texture for written presets:** `CharGen.LoadCharacter` needs `Textures\CharGen\Exported\<name>.dds`. Either composite it offline from the tint masks or let the game write it (SaveCharacter after live changes).
4. **Render fidelity:** the offline render only has to be good enough for landmarks and proportions, not for looks. Lighting and skin shading stay simple.
5. **Slider sets in BSAs:** a BSA (v105, LZ4) reader is a well-known format but adds work to M3.

## Field notes

- [skill-v2.md](face-builder/skill-v2.md): v2.1, the procedure after builds 29-75: rules, procedures for tuning a face and for building one from a picture, measurement reference, slider effects, colour facts, results and what is still missing. Supersedes v1.
- [skill-draft-v1.md](face-builder/skill-draft-v1.md): the in-game agent's procedure, measurement method (MediaPipe Face Landmarker, 13 ratios, 5% noise floor) and slider responses from the first fitting pass, 2026-11-17. [face_measure.py](face-builder/face_measure.py) is its measurement script.

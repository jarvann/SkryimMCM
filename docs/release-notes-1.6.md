# SkyLink AI v1.6.0 "Characters"

The AI can now make characters that stay in your game: relatives, followers and people to marry, with your own face or a
template's, living where you choose. It can walk you through your family's story, build them a homestead, and turn a character
into a mod of their own. 21 new tools, about 200 in all. Tested on Steam 1.7.104 (a large Vortex collection) and GOG 1.6.1179
(through MO2).

## Characters

- **SpawnNpc** makes a lasting character next to the player: race by the name the game shows, editor ID or form ID; the
  template's face, or the player's own face with their outfit and body preset; voice, outfit, essential and schedule. Before
  anything is made, the AI asks whether they follow, whether they can marry, who they're attracted to (only when an attraction
  mod is installed) and where they live. A dry run says what would be made.
- **NPC slots:** the optional `SkyLinkAI.esp` (a light plugin, chosen in the installer and on by default) gives each made
  character a slot, so saves keep them. Faces are right at once, after a save and reload, and after a restart, MO2 included.
  **ListNpcSlots** lists the slots in use across saves; **RemoveSpawnedNpc** removes a made character (never one of the game's
  own) and dismisses a follower first.
- **Followers and marriage:** a character made a follower is recruited with the game's own dialogue; **MakeFollower** turns it
  on or off. A marryable character can be proposed to with the Amulet of Mara. Voices without follower or proposal lines are
  refused or replaced. **SetAttraction** writes the player's answer into each attraction mod installed.
- **Player Family:** **SetFamilyRole** gives a character a family role (any race); relatives take after the player in face and
  hair colour. Marriage is allowed only in roles that aren't kin by blood: a step-sibling can marry, a blood relative can't.
- **FamilyStory** asks the family's history one question at a time and keeps it per character. When it's told, it suggests the
  region and style of home that fit, the homes usable now, and the relatives still to make.

## Homes

- **SetHome** moves a character to a house the player owns, the family home, a SkyLink-built home, or where they stand.
  **SetFamilyHome** keeps the family's homes. **SetSpouseHome** moves a spouse to a house the player owns or a SkyLink home,
  and the game's "visit your spouse's house" marker follows.
- **BuildHomeLayout** builds a homestead in front of the player, at its original spot, or in place of a home: a site survey moves
  it off rocks, trees and steep ground, and (asked first) the land can be cleared, never the cliffs. **SaveHomeLayout** saves a
  home's own pieces as a layout; **RemoveHomeLayout** takes a home down and gives back what was cleared.

## Your character as a mod

- **CharacterRequirements** lists the mods a made character needs (race, head parts, hair, voice, skin, face textures, hair
  physics) and the optional ones (what they wear), with Nexus pages where known.
- **ExportCharacter** writes the character as a standalone mod (a light plugin, a zip in SkyLink's Build folder, never into
  Data) with a README of its requirements, ready to install with MO2 or Vortex.
- **FinalizeCharacter** explains, for one character, what keeping them live in this save or as their own mod keeps and loses,
  and records the choice.

## Faces

- **ListFaceSliders** lists the RaceMenu sliders an install has (loose or packed in archives, MO2 mods included), for the
  player's race.
- A female character made male first in character creation kept the male makeup layers, so lip colour reached only the upper
  lip. GetFacePreset warns, and RepairFace or any face edit puts the right files back with their colours. Makeup from mods is
  left alone.
- Face edits work under MO2.

## Saves and the game

- Saves SkyLink makes are named the game's way, so the Load menu lists them under the character (shown by location and date);
  SkyLink's tools still take the label.
- The game gives the player a new character ID each time the race menu closes. SkyLink treats the IDs as one character when it
  sees the change, and **LinkCharacterIds** links older saves after the player confirms. Loading another character's save over a
  running game is refused at first.
- **ReadMessageBox** and **AnswerMessageBox** read and answer the game's message boxes; a single-button box can be closed
  automatically from then on.
- **GetGameEnvironment** names the store (Steam or GOG), versions, folders, INIs in use, mod manager and MO2 profile.
- RestartGame under MO2 waits for the game to really start. StartNewGame warns that a direct-cell start skips the opening's quests.
- GetScriptSource finds sources packed in BSA archives.

Updating from 1.5: the plugin DLL changed, so restart Skyrim after installing. New tools appear after Claude Desktop is quit
from the system tray and opened again.

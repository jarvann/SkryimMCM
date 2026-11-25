#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace NPCManager {
        json GetNearbyNPCs(float radius);
        json GetActorInfo(const std::string& formIdHex);
        json GetNPCDetailedInfo(const std::string& refFormIdHex);
        json GetFollowers();
        json GetDetectionLevel(const std::string& refFormIdHex);
        json MoveActorTo(const std::string& actorFormIdHex, const std::string& targetCellOrRefId);
        json SetRelationshipRank(const std::string& actorFormIdHex, int rank);
        json KillActor(const std::string& refFormIdHex);
        json GetCrosshairRef();
        json SetActorValue(const std::string& attribute, float value, const std::string& refId = "");
        json GetFactions(const std::string& refId = "");
        json PlayIdle(const std::string& idleFormIdHex, const std::string& refId = "");
        json GetCurrentIdle(const std::string& refId = "");
        json StopIdle(const std::string& refId = "");
        // FEAT-122 / INFRA-024: a new NPC base copied from a template of the race and sex, placed at the player. Runtime forms
        // aren't kept by saves: without SkyLink's NPC slot plugin the spawn is a preview for this session.
        json SpawnNpc(const json& params);
        json RemoveSpawnedNpc(const std::string& refId);
        // BUG-144: the second half of a removal, a few seconds after RemoveSpawnedNpc switched the character off.
        json DeleteSpawnedNpc(const std::string& refId);
        json SpawnedNpcStay(const std::string& refId);
        // Every shape on the actor's loaded head: its material, the textures the game really loaded and the tint colours.
        json HeadMaterials(const std::string& refId);
        // The parts of a look that sit on the NPC record and no face file holds: hair colour, body tone, head texture set, weight, voice.
        json GetFaceData(const std::string& refId);
        json SetFaceData(const json& params);
        // Slot characters: what saves don't keep (original race, hair colour, head texture set, voice), from slots.json. Run at
        // start-up and after loads; "rebuild" lists references whose 3D was made before it was put back.
        json ReapplySlots();
        // Voice type (form ID or editor ID) of a spawned NPC.
        json SetNpcVoice(const std::string& refId, const std::string& voice);
        // A slot character's own face file instead of its template's, from now on (the file must be where the game sees it).
        json UseOwnFace(const std::string& refId);
        // FEAT-282: makes a spawned NPC recruitable with the game's own follower dialogue (or no longer). Adults only.
        json SetFollower(const std::string& refId, bool follower);
        // FEAT-283: persistent references of the base game near a character, where an exported one can be placed.
        json HomeMarkers(const std::string& refId);
        // FEAT-296: every form the character is made of, with the plugin it comes from and what it is for.
        json CharacterForms(const std::string& refId);
        // FEAT-304: the Player Family faction's roles (its rank titles), and giving one to a spawned NPC ("none" takes it away).
        // Family members aren't marryable. Adults only.
        json FamilyRoles();
        // BUG-119: the race a SpawnNpc race text names (as SpawnNpc resolves it), for its dry run.
        json ResolveRaceText(const std::string& text);
        // BUG-123: whether a voice type (editor ID or form ID) has follower and marriage proposal lines, before spawning with it.
        json VoiceInfo(const std::string& voice);
        json SetFamily(const std::string& refId, const std::string& role);
        // A spawned NPC takes what the player wears: its own outfit removed (and its default outfit, or it comes back on load), copies
        // of the worn armour added and put on. Player-made enchantments and tempering aren't copied.
        json CopyOutfit(const std::string& refId);
        json SetSpawnedNpcEnabled(const std::string& refId, bool enable);
        // FEAT-306: the loaded mods that keep whom an NPC is attracted to, and setting it in them (women, men or everyone).
        // FEAT-307 prototype: a spawned NPC's home. Moves them there, lives there (sandbox), and makes it the place the game counts
        // as theirs (editor location and starting position: the spouse quest's house, "near editor location" packages).
        json SetHome(const std::string& refId, const std::string& atRefId);
        json AttractionMods();
        json SetAttraction(const std::string& refId, const std::string& attraction);
    }

}

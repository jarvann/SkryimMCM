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
        // FEAT-304: the Player Family faction's roles (its rank titles), and giving one to a spawned NPC ("none" takes it away).
        // Family members aren't marryable. Adults only.
        json FamilyRoles();
        json SetFamily(const std::string& refId, const std::string& role);
        // A spawned NPC takes what the player wears: its own outfit removed (and its default outfit, or it comes back on load), copies
        // of the worn armour added and put on. Player-made enchantments and tempering aren't copied.
        json CopyOutfit(const std::string& refId);
        json SetSpawnedNpcEnabled(const std::string& refId, bool enable);
    }

}

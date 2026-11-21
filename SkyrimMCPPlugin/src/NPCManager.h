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
    }

}

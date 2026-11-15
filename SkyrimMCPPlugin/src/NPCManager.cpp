#include "NPCManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <unordered_set>

#include <format>

namespace SkyrimMCP::NPCManager {

    using json = nlohmann::json;

    json GetNearbyNPCs(float radius) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto playerPos = player->GetPosition();
        json npcs = json::array();

        auto* processLists = RE::ProcessLists::GetSingleton();
        if (!processLists) return {{"error", "Process lists not available"}};

        for (auto& actorHandle : processLists->highActorHandles) {
            auto actorPtr = actorHandle.get();
            if (!actorPtr) continue;

            auto* actor = actorPtr.get();
            if (!actor || actor == player) continue;

            auto actorPos = actor->GetPosition();
            float dx = actorPos.x - playerPos.x;
            float dy = actorPos.y - playerPos.y;
            float dz = actorPos.z - playerPos.z;
            float distance = std::sqrt(dx * dx + dy * dy + dz * dz);

            if (distance > radius) continue;

            auto* base = actor->GetActorBase();
            json npc;
            npc["refId"] = std::format("{:08X}", actor->GetFormID());
            npc["baseId"] = base ? std::format("{:08X}", base->GetFormID()) : "";
            npc["name"] = actor->GetName();
            npc["race"] = base && base->GetRace() ? base->GetRace()->GetName() : "unknown";
            npc["level"] = actor->GetLevel();
            npc["distance"] = distance;
            npc["isDead"] = actor->IsDead();
            npc["isEssential"] = actor->IsEssential();
            npc["isHostile"] = actor->IsHostileToActor(player);

            npcs.push_back(npc);
        }

        return {{"npcs", npcs}};
    }

    json GetActorInfo(const std::string& formIdHex) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(formIdHex));
            if (!form) return {{"error", "Form not found: " + formIdHex}};

            auto* actor = form->As<RE::Actor>();
            if (!actor) return {{"error", "Not an actor: " + formIdHex}};

            auto* base = actor->GetActorBase();
            auto pos = actor->GetPosition();
            auto* avo = actor->AsActorValueOwner();

            json result;
            result["formId"] = formIdHex;
            result["name"] = actor->GetName();
            result["race"] = base && base->GetRace() ? base->GetRace()->GetName() : "unknown";
            result["level"] = actor->GetLevel();
            result["isDead"] = actor->IsDead();
            result["isEssential"] = actor->IsEssential();
            result["isInCombat"] = actor->IsInCombat();
            result["posX"] = pos.x;
            result["posY"] = pos.y;
            result["posZ"] = pos.z;

            if (avo) {
                result["health"] = avo->GetActorValue(RE::ActorValue::kHealth);
                result["healthMax"] = Helpers::ActorValueMax(actor, RE::ActorValue::kHealth);
            }

            auto* cell = actor->GetParentCell();
            result["cellName"] = cell ? cell->GetName() : "unknown";

            auto* player = RE::PlayerCharacter::GetSingleton();
            if (player) {
                result["isHostile"] = actor->IsHostileToActor(player);
            }

            return result;
        } catch (...) {
            return {{"error", "Invalid FormID: " + formIdHex}};
        }
    }

    json GetNPCDetailedInfo(const std::string& refFormIdHex) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
            if (!form) return {{"error", "Reference not found: " + refFormIdHex}};

            auto* actor = form->As<RE::Actor>();
            if (!actor) return {{"error", "Not an actor: " + refFormIdHex}};

            auto* base = actor->GetActorBase();
            auto* avo = actor->AsActorValueOwner();
            auto pos = actor->GetPosition();

            json result;
            result["refId"] = refFormIdHex;
            result["baseId"] = base ? std::format("{:08X}", base->GetFormID()) : "";
            result["name"] = actor->GetName();

            // Basic info
            result["race"] = base && base->GetRace() ? base->GetRace()->GetName() : "unknown";
            result["sex"] = base ? (base->GetSex() == RE::SEX::kMale ? "male" : "female") : "unknown";
            result["level"] = actor->GetLevel();

            // Class
            if (base && base->npcClass) {
                result["class"] = base->npcClass->GetName();
                result["classFormId"] = std::format("{:08X}", base->npcClass->GetFormID());
            }

            // Combat style
            if (base && base->combatStyle) {
                result["combatStyle"] = base->combatStyle->GetName() ? base->combatStyle->GetName() : "";
                result["combatStyleFormId"] = std::format("{:08X}", base->combatStyle->GetFormID());
            }

            // Outfit
            if (base && base->defaultOutfit) {
                result["outfit"] = base->defaultOutfit->GetName() ? base->defaultOutfit->GetName() : "";
                result["outfitFormId"] = std::format("{:08X}", base->defaultOutfit->GetFormID());
            }

            // Stats
            if (avo) {
                result["health"] = avo->GetActorValue(RE::ActorValue::kHealth);
                result["healthMax"] = Helpers::ActorValueMax(actor, RE::ActorValue::kHealth);
                result["magicka"] = avo->GetActorValue(RE::ActorValue::kMagicka);
                result["magickaMax"] = Helpers::ActorValueMax(actor, RE::ActorValue::kMagicka);
                result["stamina"] = avo->GetActorValue(RE::ActorValue::kStamina);
                result["staminaMax"] = Helpers::ActorValueMax(actor, RE::ActorValue::kStamina);
            }

            // State flags
            result["isDead"] = actor->IsDead();
            result["isEssential"] = actor->IsEssential();
            result["isProtected"] = actor->IsProtected();
            result["isGhost"] = actor->IsGhost();
            result["isChild"] = actor->IsChild();
            result["isPlayerTeammate"] = actor->IsPlayerTeammate();
            result["isCommandedActor"] = actor->IsCommandedActor();
            result["isInCombat"] = actor->IsInCombat();

            // Position
            result["posX"] = pos.x;
            result["posY"] = pos.y;
            result["posZ"] = pos.z;

            auto* cell = actor->GetParentCell();
            result["cellName"] = cell ? cell->GetName() : "unknown";

            // Hostility
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (player) {
                result["isHostile"] = actor->IsHostileToActor(player);
            }

            // Factions
            json factions = json::array();
            actor->VisitFactions([&](RE::TESFaction* faction, std::int8_t rank) -> bool {
                if (!faction) return true;
                try {
                    json f;
                    f["name"] = faction->GetName() ? faction->GetName() : "";
                    f["rank"] = rank;
                    factions.push_back(f);
                } catch (...) {}
                return true;
            });
            result["factions"] = factions;

            return result;
        } catch (...) {
            return {{"error", "Failed to get NPC info: " + refFormIdHex}};
        }
    }

    json GetFollowers() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* processLists = RE::ProcessLists::GetSingleton();
        if (!processLists) return {{"error", "Process lists not available"}};

        json followers = json::array();

        for (auto& actorHandle : processLists->highActorHandles) {
            auto actorPtr = actorHandle.get();
            if (!actorPtr) continue;

            auto* actor = actorPtr.get();
            if (!actor || actor == player) continue;

            if (!actor->IsPlayerTeammate()) continue;

            try {
                auto* base = actor->GetActorBase();
                auto* avo = actor->AsActorValueOwner();
                auto actorPos = actor->GetPosition();
                auto playerPos = player->GetPosition();

                float dx = actorPos.x - playerPos.x;
                float dy = actorPos.y - playerPos.y;
                float dz = actorPos.z - playerPos.z;
                float distance = std::sqrt(dx * dx + dy * dy + dz * dz);

                json f;
                f["refId"] = std::format("{:08X}", actor->GetFormID());
                f["baseId"] = base ? std::format("{:08X}", base->GetFormID()) : "";
                f["name"] = actor->GetName();
                f["race"] = base && base->GetRace() ? base->GetRace()->GetName() : "unknown";
                f["level"] = actor->GetLevel();
                f["distance"] = distance;
                f["isDead"] = actor->IsDead();
                f["isEssential"] = actor->IsEssential();
                f["isInCombat"] = actor->IsInCombat();

                if (avo) {
                    f["health"] = avo->GetActorValue(RE::ActorValue::kHealth);
                    f["healthMax"] = Helpers::ActorValueMax(actor, RE::ActorValue::kHealth);
                }

                // Class
                if (base && base->npcClass) {
                    f["class"] = base->npcClass->GetName();
                }

                followers.push_back(f);
            } catch (...) {
                continue;
            }
        }

        return {{"followers", followers}, {"count", followers.size()}};
    }

    json GetDetectionLevel(const std::string& refFormIdHex) {
        try {
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};

            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
            if (!form) return {{"error", "Reference not found: " + refFormIdHex}};

            auto* actor = form->As<RE::Actor>();
            if (!actor) return {{"error", "Not an actor: " + refFormIdHex}};

            // Request detection level — returns 0-100 where:
            // -1000 = undetected, 0 = lost, 1-99 = suspicious, 100 = fully detected
            auto level = actor->RequestDetectionLevel(player);

            std::string state;
            if (level <= -1) state = "undetected";
            else if (level == 0) state = "lost";
            else if (level < 100) state = "suspicious";
            else state = "detected";

            return {{"refId", refFormIdHex},
                    {"name", actor->GetName()},
                    {"detectionLevel", level},
                    {"state", state}};
        } catch (...) {
            return {{"error", "Failed to get detection level: " + refFormIdHex}};
        }
    }

    json MoveActorTo(const std::string& actorFormIdHex, const std::string& targetCellOrRefId) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(actorFormIdHex));
            if (!form) return {{"error", "Actor not found: " + actorFormIdHex}};

            auto* actor = form->As<RE::Actor>();
            if (!actor) return {{"error", "Not an actor: " + actorFormIdHex}};

            // Default: move to player
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};

            if (targetCellOrRefId.empty() || targetCellOrRefId == "player") {
                actor->MoveTo(player);
            } else {
                // Try to look up target as a reference
                auto* targetForm = RE::TESForm::LookupByID(Helpers::ParseFormId(targetCellOrRefId));
                if (targetForm) {
                    auto* targetRef = targetForm->As<RE::TESObjectREFR>();
                    if (targetRef) {
                        actor->MoveTo(targetRef);
                    } else {
                        return {{"error", "Target is not a reference: " + targetCellOrRefId}};
                    }
                } else {
                    // Move to player as fallback
                    actor->MoveTo(player);
                }
            }

            return {{"moved", true}, {"actor", actorFormIdHex}, {"name", actor->GetName()}};
        } catch (...) {
            return {{"error", "Failed to move actor: " + actorFormIdHex}};
        }
    }

    json SetRelationshipRank(const std::string& actorFormIdHex, int rank) {
        try {
            auto* actor = Helpers::ResolveActor(actorFormIdHex);
            if (!actor) return {{"error", "Actor not found: " + actorFormIdHex}};

            auto* actorBase = actor->GetActorBase();
            if (!actorBase) return {{"error", "Actor has no base form"}};

            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};
            auto* playerBase = player->GetActorBase();
            if (!playerBase) return {{"error", "Player base not available"}};

            auto* rel = RE::BGSRelationship::GetRelationship(playerBase, actorBase);
            if (!rel) return {{"error", "No relationship found with: " + actorFormIdHex}};

            auto oldLevel = static_cast<int>(rel->level.get());
            rel->level = static_cast<RE::BGSRelationship::RELATIONSHIP_LEVEL>(rank);

            json result;
            result["set"] = true;
            result["actor"] = actor->GetName();
            result["oldRank"] = oldLevel;
            result["newRank"] = rank;
            return result;
        } catch (...) {
            return {{"error", "Failed to set relationship: " + actorFormIdHex}};
        }
    }

    json KillActor(const std::string& refFormIdHex) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
            if (!form) return {{"error", "Reference not found: " + refFormIdHex}};

            auto* actor = form->As<RE::Actor>();
            if (!actor) return {{"error", "Not an actor: " + refFormIdHex}};

            if (actor->IsDead()) return {{"error", "Actor is already dead"}};

            bool wasEssential = actor->IsEssential();
            bool wasProtected = actor->IsProtected();

            // Remove essential flag to allow kill (runtime flag, one-shot)
            if (wasEssential) {
                auto& flags = actor->GetActorRuntimeData().boolFlags;
                flags.reset(RE::Actor::BOOL_FLAGS::kEssential);
            }

            actor->KillImmediate();

            return {{"killed", true}, {"refId", refFormIdHex}, {"name", actor->GetName()},
                    {"wasEssential", wasEssential}, {"wasProtected", wasProtected}};
        } catch (...) {
            return {{"error", "Failed to kill actor: " + refFormIdHex}};
        }
    }

    json GetCrosshairRef() {
        auto* pickData = RE::CrosshairPickData::GetSingleton();
        if (!pickData) return {{"error", "Crosshair pick data not available"}};

        json result;

        // Check for targeted object/actor
        auto targetHandle = pickData->target;
        auto targetPtr = targetHandle.get();
        if (targetPtr) {
            auto* ref = targetPtr.get();
            if (ref) {
                result["refId"] = std::format("{:08X}", ref->GetFormID());
                auto* baseForm = ref->GetBaseObject();
                result["baseId"] = baseForm ? std::format("{:08X}", baseForm->GetFormID()) : "";
                result["name"] = ref->GetName();

                // Check if it's an actor
                auto* actor = ref->As<RE::Actor>();
                if (actor) {
                    result["type"] = "actor";
                    result["isActor"] = true;
                    auto* base = actor->GetActorBase();
                    if (base && base->GetRace()) result["race"] = base->GetRace()->GetName();
                    result["level"] = actor->GetLevel();
                    result["isDead"] = actor->IsDead();
                    result["isEssential"] = actor->IsEssential();
                } else {
                    result["type"] = "object";
                    result["isActor"] = false;
                }
                return result;
            }
        }

        return {{"error", "Nothing targeted — look at an NPC or object"}};
    }

    json SetActorValue(const std::string& attribute, float value, const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", refId.empty() ? "Player not available" : "Actor not found: " + refId}};

        auto av = Helpers::ResolveActorValue(attribute);
        if (!av) return {{"error", "Unknown attribute: " + attribute}};

        auto* avo = actor->AsActorValueOwner();
        if (!avo) return {{"error", "Actor value owner not available"}};

        avo->SetActorValue(*av, value);

        json result;
        result["set"] = true;
        result["attribute"] = attribute;
        result["value"] = value;
        if (!refId.empty() && refId != "player") {
            result["actor"] = refId;
        }
        return result;
    }

    json GetFactions(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", refId.empty() ? "Player not available" : "Actor not found: " + refId}};

        json factions = json::array();
        std::unordered_set<RE::FormID> seen;

        auto addFaction = [&](RE::TESFaction* faction, std::int8_t rank) {
            if (!faction || seen.count(faction->GetFormID())) return;
            seen.insert(faction->GetFormID());
            try {
                json f;
                f["formId"] = std::format("{:08X}", faction->GetFormID());
                f["name"] = faction->GetName() ? faction->GetName() : "";
                f["editorId"] = faction->GetFormEditorID() ? faction->GetFormEditorID() : "";
                f["rank"] = rank;
                factions.push_back(f);
            } catch (...) {}
        };

        // 1. Base TESNPC factions (from race/class/record)
        auto* base = actor->GetActorBase();
        if (base) {
            for (auto& factionRank : base->factions) {
                if (factionRank.faction) {
                    addFaction(factionRank.faction, factionRank.rank);
                }
            }
        }

        // 2. Runtime faction changes (via ExtraData)
        auto* extraList = &actor->extraList;
        if (extraList) {
            auto* factionChanges = extraList->GetByType<RE::ExtraFactionChanges>();
            if (factionChanges) {
                for (auto& factionRank : factionChanges->factionChanges) {
                    if (factionRank.faction) {
                        addFaction(factionRank.faction, factionRank.rank);
                    }
                }
            }
        }

        // 3. VisitFactions as fallback for any we missed
        actor->VisitFactions([&](RE::TESFaction* faction, std::int8_t rank) -> bool {
            if (faction) addFaction(faction, rank);
            return true;
        });

        json result;
        result["factions"] = factions;
        result["count"] = factions.size();
        if (!refId.empty() && refId != "player") {
            result["refId"] = refId;
            result["name"] = actor->GetName();
        }
        return result;
    }

    json PlayIdle(const std::string& idleFormIdHex, const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};

        auto* idle = RE::TESForm::LookupByID<RE::TESIdleForm>(Helpers::ParseFormId(idleFormIdHex));
        if (!idle) return {{"error", "Idle form not found: " + idleFormIdHex}};

        auto* process = actor->GetActorRuntimeData().currentProcess;
        if (!process) return {{"error", "Actor has no AI process (too far away?)"}};

        bool played = process->PlayIdle(actor, idle, nullptr);
        auto refIdStr = std::format("{:08X}", actor->GetFormID());
        json result;
        result["success"] = played;
        result["target"] = actor->GetName();
        result["targetRefId"] = refIdStr;
        result["idleFormId"] = idleFormIdHex;
        result["idleName"] = idle->GetFormEditorID() ? idle->GetFormEditorID() : "";
        result["animFile"] = idle->animFileName.c_str() ? idle->animFileName.c_str() : "";
        result["animEvent"] = idle->animEventName.c_str() ? idle->animEventName.c_str() : "";
        return result;
    }

    json GetCurrentIdle(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};

        auto* process = actor->GetActorRuntimeData().currentProcess;
        if (!process) return {{"error", "Actor has no AI process"}};

        auto* high = process->high;
        if (!high) return {{"currentIdle", nullptr}, {"target", actor->GetName()}};

        auto* idle = high->currentProcessIdle;
        if (!idle) return {{"currentIdle", nullptr}, {"target", actor->GetName()}};

        auto actorRefId = std::format("{:08X}", actor->GetFormID());
        auto idleFormId = std::format("{:08X}", idle->GetFormID());
        json idleInfo;
        idleInfo["formId"] = idleFormId;
        idleInfo["editorId"] = idle->GetFormEditorID() ? idle->GetFormEditorID() : "";
        idleInfo["animFile"] = idle->animFileName.c_str() ? idle->animFileName.c_str() : "";
        idleInfo["animEvent"] = idle->animEventName.c_str() ? idle->animEventName.c_str() : "";

        json result;
        result["target"] = actor->GetName();
        result["targetRefId"] = actorRefId;
        result["currentIdle"] = idleInfo;
        return result;
    }

    json StopIdle(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};

        auto* process = actor->GetActorRuntimeData().currentProcess;
        if (!process) return {{"error", "Actor has no AI process"}};

        process->StopCurrentIdle(actor, true);
        auto refIdStr = std::format("{:08X}", actor->GetFormID());
        json result;
        result["stopped"] = true;
        result["target"] = actor->GetName();
        result["targetRefId"] = refIdStr;
        return result;
    }

}

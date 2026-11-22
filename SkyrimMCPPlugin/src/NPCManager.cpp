#include "NPCManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <unordered_set>
#include <vector>

#include <cmath>
#include <numbers>
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

            // The game's own kill routine, the one the console's kill and Papyrus Actor.Kill use:
            // death event, death animation and ragdoll. KillImmediate only flagged the actor dead
            // on 1.7.x, leaving it standing and animating (seen 2026-11-16). No attacker, so no crime.
            actor->KillImpl(nullptr, 0.0f, true, false);

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


    namespace {
        RE::TESRace* ResolveRace(const std::string& text) {
            if (text.empty()) return nullptr;
            RE::TESForm* form = nullptr;
            try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(text)); } catch (...) {}
            if (!form) form = RE::TESForm::LookupByEditorID(text);
            return form ? form->As<RE::TESRace>() : nullptr;
        }

        // INFRA-024: SkyLinkAI.esl (optional) holds ten NPC slots of each sex, object IDs 0x800-0x809 female and 0x80A-0x813 male.
        // Their base forms belong to a plugin, so the game can keep changes to them and references to them in saves.
        constexpr std::string_view kSlotPlugin = "SkyLinkAI.esl";
        constexpr RE::FormID kFirstSlot = 0x800;
        constexpr int kSlotsPerSex = 10;

        // The plugin and object ID a template's FaceGen files are named by: its own, or the NPC it borrows its face from.
        json FaceSource(RE::TESNPC* npc) {
            auto* from = npc ? npc->GetRootFaceNPC() : nullptr;
            if (!from) from = npc;
            auto* file = from ? from->GetFile(0) : nullptr;
            if (!file) return nullptr;
            const auto local = file->IsLight() ? (from->GetFormID() & 0xFFF) : (from->GetFormID() & 0xFFFFFF);
            return {{"plugin", std::string(file->GetFilename())}, {"objectId", std::format("{:08X}", local)}};
        }

        std::string SlotDefaultName(int index) {
            const bool female = index < kSlotsPerSex;
            return std::format("SkyLink Character {}{:02d}", female ? 'F' : 'M', (female ? index : index - kSlotsPerSex) + 1);
        }

        int SlotIndex(const RE::TESForm* form) {
            auto* data = RE::TESDataHandler::GetSingleton();
            if (!data || !form) return -1;
            for (int i = 0; i < kSlotsPerSex * 2; ++i)
                if (data->LookupForm(kFirstSlot + i, kSlotPlugin) == form) return i;
            return -1;
        }

        bool SlotPluginLoaded() {
            auto* data = RE::TESDataHandler::GetSingleton();
            return data && data->LookupForm(kFirstSlot, kSlotPlugin) != nullptr;
        }

        // A slot is free while it still has its own name (SkyLink renames it when it fills one; the game saves that change).
        RE::TESNPC* FreeSlot(bool female, int& index, const std::vector<int>& avoid) {
            auto* data = RE::TESDataHandler::GetSingleton();
            if (!data) return nullptr;
            const int first = female ? 0 : kSlotsPerSex;
            for (int i = first; i < first + kSlotsPerSex; ++i) {
                // Reserved in SkyLink's registry: the slot's face files belong to a character in some save.
                if (std::find(avoid.begin(), avoid.end(), i) != avoid.end()) continue;
                auto* npc = data->LookupForm<RE::TESNPC>(kFirstSlot + i, kSlotPlugin);
                if (npc && SlotDefaultName(i) == npc->GetName()) {
                    index = i;
                    return npc;
                }
            }
            return nullptr;
        }

        // A plain NPC of this race and sex to copy: not the player, not built from a template (its look and stats would come
        // from somewhere else), preferring one that isn't unique.
        // (2026-11-22: the first version took the first match in the form list, which for a Nord man was the Headless Horseman.)
        RE::TESNPC* FindTemplate(RE::TESRace* race, bool female) {
            auto* data = RE::TESDataHandler::GetSingleton();
            if (!data || !race) return nullptr;
            constexpr RE::FormID kCitizenClass = 0x0001326B;
            using Flag = RE::ACTOR_BASE_DATA::Flag;
            RE::TESNPC* best = nullptr;
            int bestScore = -1;
            for (auto* npc : data->GetFormArray<RE::TESNPC>()) {
                if (!npc || npc->GetFormID() == 0x7 || npc->race != race) continue;
                if ((npc->GetSex() == RE::SEX::kFemale) != female) continue;
                if (npc->baseTemplateForm || npc->IsDeleted() || SlotIndex(npc) >= 0) continue;
                const auto& flags = npc->actorData.actorBaseFlags;
                if (flags.any(Flag::kIsGhost, Flag::kSummonable, Flag::kInvulnerable, Flag::kSimpleActor, Flag::kDoesntBleed,
                              Flag::kIsChargenFacePreset, Flag::kUsesTemplate)) continue;
                // No head of its own, no name, or a plugin's script on it: not an ordinary person to copy.
                if (npc->numHeadParts < 3 || !npc->GetName() || !*npc->GetName()) continue;
                const auto id = npc->GetFormID();
                int score = 0;
                if ((id >> 24) == 0x00) score += 2;                            // the base game
                if (npc->npcClass && npc->npcClass->GetFormID() == kCitizenClass) score += 3;
                if (!npc->IsUnique()) score += 2;
                if (npc->defaultOutfit) score += 1;
                if (!flags.any(Flag::kEssential, Flag::kProtected)) score += 1;
                if (score > bestScore) {
                    bestScore = score;
                    best = npc;
                }
            }
            return best;
        }
    }

    namespace {
        // 2026-11-22: a copy of Dawnstar's court wizard walked home to Dawnstar, schedule, job factions and all. A spawned
        // character stays around where it stands and belongs to no one's faction until SkyLink gives it one.
        void MakeStayPut(RE::TESNPC* npc) {
            npc->aiPackages.packages.clear();
            if (auto* stay = RE::TESForm::LookupByID<RE::TESPackage>(0x000956B8)) npc->aiPackages.packages.push_front(stay);  // DefaultSandboxCurrentLocation256
            npc->factions.clear();
            npc->crimeFaction = nullptr;
        }
    }

    namespace {
        // In front of the player, facing them, and not through a wall: PlaceObjectAtMe puts it on the player's own spot (in a
        // photo the player's hair was in the way). Returns how far in front it ended up.
        float PlaceInFront(RE::TESObjectREFR* ref, RE::PlayerCharacter* player, float distance) {
            const float facing = player->GetAngleZ();
            const RE::NiPoint3 dir{ std::sin(facing), std::cos(facing), 0.0f };
            const auto at = player->GetPosition();
            if (auto* cell = player->GetParentCell(); auto* world = cell ? cell->GetbhkWorld() : nullptr) {
                const float scale = RE::bhkWorld::GetWorldScale();
                RE::bhkPickData pick;
                pick.rayInput.from = (at + RE::NiPoint3{ 0, 0, 70.0f }) * scale;
                pick.rayInput.to = (at + RE::NiPoint3{ 0, 0, 70.0f } + dir * (distance + 40.0f)) * scale;
                player->GetCollisionFilterInfo(pick.rayInput.filterInfo);
                pick.rayInput.filterInfo.SetCollisionLayer(RE::COL_LAYER::kLOS);
                RE::BSReadLockGuard lock(world->worldLock);
                if (world->PickObject(pick); pick.rayOutput.HasHit())
                    distance = std::max(0.0f, pick.rayOutput.hitFraction * (distance + 40.0f) - 40.0f);
            }
            ref->SetPosition(at + dir * distance);
            ref->SetAngle(RE::NiPoint3{ 0.0f, 0.0f, std::fmod(facing + std::numbers::pi_v<float>, 2 * std::numbers::pi_v<float>) });
            return distance;
        }
    }

    // Puts the stay-put behaviour on a character already spawned (and stops what it is doing).
    json SpawnedNpcStay(const std::string& refId) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc || ((npc->GetFormID() >> 24) != 0xFF && SlotIndex(npc) < 0)) return {{"error", "Not a spawned NPC: " + refId}};
        MakeStayPut(npc);
        using CF = RE::TESNPC::ChangeFlags;
        if (SlotIndex(npc) >= 0) npc->AddChange(CF::kAIData | CF::kFactions);
        actor->EvaluatePackage(true, false);
        return {{"refId", refId}, {"stays", true}};
    }

    json SetNpcVoice(const std::string& refId, const std::string& voice) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc || ((npc->GetFormID() >> 24) != 0xFF && SlotIndex(npc) < 0)) return {{"error", "Not a spawned NPC: " + refId}};
        RE::TESForm* v = RE::TESForm::LookupByEditorID(voice);
        if (!v) try { v = RE::TESForm::LookupByID(Helpers::ParseFormId(voice)); } catch (...) {}
        auto* type = v ? v->As<RE::BGSVoiceType>() : nullptr;
        if (!type) return {{"error", "Not a voice type: " + voice}};
        auto* old = npc->voiceType;
        npc->voiceType = type;
        auto label = [](RE::BGSVoiceType* t) { return t && t->GetFormEditorID() ? json(t->GetFormEditorID()) : json(nullptr); };
        return {{"refId", refId}, {"was", label(old)}, {"voice", label(type)}, {"voiceId", std::format("{:08X}", type->GetFormID())}};
    }

    json HeadMaterials(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        auto* face = actor && actor->Get3D(false) ? actor->GetFaceNodeSkinned() : nullptr;
        if (!face) return {{"error", "That actor's head isn't loaded"}};
        auto name = [](const RE::NiPointer<RE::NiSourceTexture>& t) -> json {
            if (!t) return nullptr;
            return t->name.c_str() ? json(t->name.c_str()) : json("(unnamed)");
        };
        auto colour = [](const RE::NiColor& c) {
            return std::format("#{:02X}{:02X}{:02X}", static_cast<int>(std::clamp(c.red, 0.0f, 1.0f) * 255), static_cast<int>(std::clamp(c.green, 0.0f, 1.0f) * 255),
                               static_cast<int>(std::clamp(c.blue, 0.0f, 1.0f) * 255));
        };
        json shapes = json::array();
        RE::BSVisit::TraverseScenegraphGeometries(face, [&](RE::BSGeometry* geo) {
            json s = {{"shape", geo->name.c_str() ? geo->name.c_str() : ""}};
            auto* shader = geo->lightingShaderProp_cast();
            auto* material = shader ? static_cast<RE::BSLightingShaderMaterialBase*>(shader->material) : nullptr;
            if (material) {
                const auto feature = material->GetFeature();
                s["feature"] = static_cast<int>(feature);
                s["diffuse"] = name(material->diffuseTexture);
                if (feature == RE::BSShaderMaterial::Feature::kFaceGen) {
                    auto* m = static_cast<RE::BSLightingShaderMaterialFacegen*>(material);
                    s["tint"] = name(m->tintTexture);
                    s["detail"] = name(m->detailTexture);
                } else if (feature == RE::BSShaderMaterial::Feature::kHairTint) {
                    s["tintColor"] = colour(static_cast<RE::BSLightingShaderMaterialHairTint*>(material)->tintColor);
                } else if (feature == RE::BSShaderMaterial::Feature::kFaceGenRGBTint) {
                    s["tintColor"] = colour(static_cast<RE::BSLightingShaderMaterialFacegenTint*>(material)->tintColor);
                }
            }
            shapes.push_back(s);
            return RE::BSVisit::BSVisitControl::kContinue;
        });
        json result = {{"refId", refId}, {"shapes", shapes}};
        if (auto* npc = actor->GetActorBase()) {
            result["bodyTint"] = std::format("#{:02X}{:02X}{:02X}", npc->bodyTintColor.red, npc->bodyTintColor.green, npc->bodyTintColor.blue);
            result["faceNPC"] = npc->faceNPC ? json(std::format("{:08X}", npc->faceNPC->GetFormID())) : json(nullptr);
            auto* hair = npc->headRelatedData ? npc->headRelatedData->hairColor : nullptr;
            result["hairColor"] = hair ? json(std::format("{:08X} #{:02X}{:02X}{:02X}", hair->GetFormID(), hair->color.red, hair->color.green, hair->color.blue)) : json(nullptr);
            result["voice"] = npc->voiceType && npc->voiceType->GetFormEditorID() ? json(npc->voiceType->GetFormEditorID()) : json(nullptr);
        }
        return result;
    }

    json SpawnNpc(const json& params) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* playerBase = player ? player->GetActorBase() : nullptr;
        if (!player || !playerBase) return {{"error", "No player (load a game first)"}};

        // A named template gives its race and sex unless the call says otherwise; without one, the player's.
        RE::TESNPC* asked = nullptr;
        if (auto t = params.value("template", std::string()); !t.empty()) {
            RE::TESForm* form = nullptr;
            try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(t)); } catch (...) {}
            if (!form) form = RE::TESForm::LookupByEditorID(t);
            asked = form ? form->As<RE::TESNPC>() : nullptr;
            if (!asked) return {{"error", "Template isn't an NPC base: " + t}};
        }
        const auto* like = asked ? asked : playerBase;
        auto* race = ResolveRace(params.value("race", std::string()));
        if (!race) race = like->race;
        const bool female = params.contains("female") && params["female"].is_boolean()
            ? params["female"].get<bool>() : like->GetSex() == RE::SEX::kFemale;

        // RaceMenu's preset is the player's: another race or sex would get a face that doesn't fit the body.
        if (params.value("face", std::string("template")) == "player" &&
            (race != playerBase->race || female != (playerBase->GetSex() == RE::SEX::kFemale)))
            return {{"error", "face 'player' needs the player's own race and sex (the face is theirs). Use face 'template' for another race or sex."}};

        RE::TESNPC* tmpl = asked;
        if (!tmpl) {
            tmpl = FindTemplate(race, female);
            if (!tmpl) return {{"error", std::format("No NPC of race {:08X} ({}) to copy from", race->GetFormID(), female ? "female" : "male")}};
        }

        // A slot of SkyLink's optional plugin when one is free (kept by saves), else a runtime form (this session only).
        int slotIndex = -1;
        std::vector<int> avoid;
        if (params.contains("avoidSlots") && params["avoidSlots"].is_array())
            for (const auto& a : params["avoidSlots"]) if (a.is_number_integer()) avoid.push_back(a.get<int>());
        RE::TESNPC* npc = params.value("useSlot", true) ? FreeSlot(female, slotIndex, avoid) : nullptr;
        if (!npc) {
            auto* factory = RE::IFormFactory::GetConcreteFormFactoryByType<RE::TESNPC>();
            npc = factory ? factory->Create() : nullptr;
        }
        if (!npc) return {{"error", "The game's NPC form factory didn't create a form"}};
        npc->Copy(tmpl);
        npc->race = race;
        if (!params.value("keepSchedule", false)) MakeStayPut(npc);

        const auto name = params.value("name", std::string());
        if (!name.empty()) npc->SetFullName(name.c_str());
        // A copy of a unique NPC would share its "only one of me" rule; a spawned character is its own person.
        npc->actorData.actorBaseFlags.reset(RE::ACTOR_BASE_DATA::Flag::kUnique);
        if (params.value("essential", false)) npc->actorData.actorBaseFlags.set(RE::ACTOR_BASE_DATA::Flag::kEssential);

        // The face: the template's own FaceGen (it has a file), or none, for RaceMenu's CharGen to build one.
        const auto face = params.value("face", std::string("template"));
        npc->faceNPC = face == "template" ? (tmpl->faceNPC ? tmpl->faceNPC : tmpl) : nullptr;

        // Spike 2026-11-22 (Steam): CharGen.LoadCharacter copies head parts and morphs but not the hair colour or body tone, so
        // take the player's. The copy must not write through pointers the template still owns.
        const bool sharedHead = npc->headRelatedData && npc->headRelatedData == tmpl->headRelatedData;
        if (face == "player") {
            npc->bodyTintColor = playerBase->bodyTintColor;
            if (auto* from = playerBase->headRelatedData ? playerBase->headRelatedData->hairColor : nullptr) {
                if (sharedHead) npc->headRelatedData = nullptr;
                // Its own colour form: RaceMenu edits the player's in place.
                auto* colors = RE::IFormFactory::GetConcreteFormFactoryByType<RE::BGSColorForm>();
                auto* own = colors ? colors->Create() : nullptr;
                if (own) {
                    own->color = from->color;
                    own->flags = from->flags;
                }
                npc->SetHairColor(own ? own : from);
            }
        }

        const bool slot = slotIndex >= 0;
        if (slot) {
            // Mark what changed on the slot so the game writes it into saves.
            using CF = RE::TESNPC::ChangeFlags;
            npc->AddChange(CF::kBaseData | CF::kAttributes | CF::kAIData | CF::kFullName | CF::kFactions | CF::kNPCSkills |
                           CF::kClass | CF::kFace | CF::kDefaultOutfit | CF::kGender | CF::kRace);
        }

        auto ref = player->PlaceObjectAtMe(npc, slot);
        if (!ref) return {{"error", "PlaceObjectAtMe returned nothing"}};
        const float placedAt = PlaceInFront(ref.get(), player, std::clamp(params.value("distance", 160.0f), 60.0f, 600.0f));

        return {
            {"refId", std::format("{:08X}", ref->GetFormID())},
            {"baseId", std::format("{:08X}", npc->GetFormID())},
            {"name", npc->GetName()},
            {"race", std::format("{:08X}", race->GetFormID())},
            {"raceName", race->GetName()},
            {"female", female},
            {"template", std::format("{:08X}", tmpl->GetFormID())},
            {"templateName", tmpl->GetName()},
            {"face", face},
            {"templateSharedHeadData", sharedHead},
            {"runtimeForm", (npc->GetFormID() >> 24) == 0xFF},
            {"slot", slot ? json(slotIndex) : json(nullptr)},
            {"slotObjectId", slot ? json(std::format("{:08X}", kFirstSlot + slotIndex)) : json(nullptr)},
            {"faceSource", FaceSource(tmpl)},
            {"distance", placedAt},
            {"slotPluginLoaded", SlotPluginLoaded()},
        };
    }

    // Disable, then enable again in a later task, rebuilds the 3D: CharGen.LoadCharacter changes the base, but the head already
    // built stays as it was (the spike's NPC kept her template's head until this).
    json SetSpawnedNpcEnabled(const std::string& refId, bool enable) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* ref = form ? form->As<RE::TESObjectREFR>() : nullptr;
        auto* base = ref ? ref->GetBaseObject() : nullptr;
        if (!ref || !base || ((base->GetFormID() >> 24) != 0xFF && SlotIndex(base) < 0)) return {{"error", "Not a spawned NPC: " + refId}};
        if (enable) ref->Enable(false); else ref->Disable();
        return {{"refId", refId}, {"enabled", enable}};
    }

    json RemoveSpawnedNpc(const std::string& refId) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* ref = form ? form->As<RE::TESObjectREFR>() : nullptr;
        if (!ref) return {{"error", "Reference not found: " + refId}};
        auto* base = ref->GetBaseObject();
        // Only what SkyLink made: a runtime base (FF...) or one of its slots.
        const int slot = SlotIndex(base);
        if (!base || ((base->GetFormID() >> 24) != 0xFF && slot < 0)) return {{"error", "Not a spawned NPC (its base isn't SkyLink's): " + refId}};
        ref->Disable();
        ref->SetDelete(true);
        if (slot >= 0) {
            // The slot is free again once it has its own name back.
            if (auto* npc = base->As<RE::TESNPC>()) {
                npc->SetFullName(SlotDefaultName(slot).c_str());
                npc->AddChange(RE::TESNPC::ChangeFlags::kFullName);
            }
        }
        return {{"removed", true}, {"refId", refId}};
    }
}

#include "NPCManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <unordered_set>
#include <vector>

#include <cctype>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <numbers>
#include <optional>
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

        // INFRA-024: SkyLinkAI.esp (optional) holds ten NPC slots of each sex, object IDs 0x800-0x809 female and 0x80A-0x813 male.
        // Their base forms belong to a plugin, so the game can keep changes to them and references to them in saves.
        constexpr std::string_view kSlotPlugin = "SkyLinkAI.esp";
        constexpr RE::FormID kFirstSlot = 0x800;
        // Slots by race (the game uses a face file only while an NPC's race is the one its record was made with), then sex, then
        // number. The same order as scripts/build-slot-plugin.py; it must never change once released.
        struct SlotRace { RE::FormID id; const char* name; };
        constexpr SlotRace kSlotRaces[] = {
            { 0x13741, "Breton" }, { 0x13746, "Nord" }, { 0x13744, "Imperial" }, { 0x13748, "Redguard" }, { 0x13743, "High Elf" },
            { 0x13742, "Dark Elf" }, { 0x13749, "Wood Elf" }, { 0x13747, "Orc" }, { 0x13745, "Khajiit" }, { 0x13740, "Argonian" },
        };
        constexpr int kSlotRaceCount = static_cast<int>(std::size(kSlotRaces));
        constexpr int kSlotsPerSex = 25;
        constexpr int kSlotCount = kSlotRaceCount * 2 * kSlotsPerSex;

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
            const int inRace = index % (2 * kSlotsPerSex);
            return std::format("SkyLink {} {}{:02d}", kSlotRaces[index / (2 * kSlotsPerSex)].name, inRace < kSlotsPerSex ? 'F' : 'M', inRace % kSlotsPerSex + 1);
        }

        int SlotIndex(const RE::TESForm* form) {
            auto* data = RE::TESDataHandler::GetSingleton();
            if (!data || !form) return -1;
            // By its plugin and local ID (asked for every NPC in the game when looking for a template).
            auto* file = form->GetFile(0);
            if (!file || file->GetFilename() != kSlotPlugin) return -1;
            const int i = static_cast<int>(form->GetFormID() & 0xFFF) - static_cast<int>(kFirstSlot);
            return i >= 0 && i < kSlotCount && data->LookupForm(kFirstSlot + i, kSlotPlugin) == form ? i : -1;
        }

        bool SlotPluginLoaded() {
            auto* data = RE::TESDataHandler::GetSingleton();
            return data && data->LookupForm(kFirstSlot, kSlotPlugin) != nullptr;
        }

        // A slot is free while it still has its own name (SkyLink renames it when it fills one; the game saves that change).
        // The slots of that race and sex first. A race without its own (vampires, modded races) uses its parent race's or a Nord's,
        // with the original race set on the slot (ReapplySlots).
        // prefer: slots whose face files the game listed at start (FEAT-305), taken first so a new face shows at once.
        RE::TESNPC* FreeSlot(RE::TESRace* race, bool female, int& index, const std::vector<int>& avoid, const std::vector<int>& prefer) {
            auto* data = RE::TESDataHandler::GetSingleton();
            if (!data) return nullptr;
            auto block = [](const RE::TESRace* r) {
                for (int k = 0; r && k < kSlotRaceCount; ++k)
                    if (kSlotRaces[k].id == r->GetFormID()) return k;
                return -1;
            };
            int raceBlock = block(race);
            if (raceBlock < 0 && race) raceBlock = block(race->armorParentRace);
            if (raceBlock < 0) raceBlock = 1;
            const int first = raceBlock * 2 * kSlotsPerSex + (female ? 0 : kSlotsPerSex);
            auto listed = [](const std::vector<int>& list, int i) { return std::find(list.begin(), list.end(), i) != list.end(); };
            for (const bool preferredOnly : { true, false }) {
                // BUG-112: with nothing preferred (no stand-ins known yet, a fresh install's first session) only the second pass runs.
                if (preferredOnly && prefer.empty()) continue;
                for (int i = first; i < first + kSlotsPerSex; ++i) {
                    // Reserved in SkyLink's registry: the slot's face files belong to a character in some save.
                    if (listed(avoid, i) || (preferredOnly && !listed(prefer, i))) continue;
                    auto* npc = data->LookupForm<RE::TESNPC>(kFirstSlot + i, kSlotPlugin);
                    if (npc && SlotDefaultName(i) == npc->GetName()) {
                        index = i;
                        return npc;
                    }
                }
            }
            return nullptr;
        }

        // "Test" or "Dummy" as a word of its own or a camel-case part of an editor ID ("TestGuyArgonian", "WarehouseTestGuy"):
        // capitalised, anything before it and no lower-case letter after it ("Testament" passes); lower-case "test" only as a
        // separate word ("Contest", "attest" pass).
        bool IsTestActor(const char* text) {
            if (!text) return false;
            const std::string_view s(text);
            const auto lower = [&](size_t i) { return i < s.size() && std::islower(static_cast<unsigned char>(s[i])); };
            const auto letter = [&](size_t i) { return i < s.size() && std::isalpha(static_cast<unsigned char>(s[i])); };
            for (std::string_view word : { std::string_view("Test"), std::string_view("Dummy"), std::string_view("test") }) {
                const bool capital = std::isupper(static_cast<unsigned char>(word[0]));
                for (size_t at = s.find(word); at != std::string_view::npos; at = s.find(word, at + 1)) {
                    const size_t end = at + word.size();
                    if (capital ? !lower(end) : ((at == 0 || !letter(at - 1)) && !letter(end))) return true;
                }
            }
            return false;
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
            int bestScore = -100;
            for (auto* npc : data->GetFormArray<RE::TESNPC>()) {
                if (!npc || npc->GetFormID() == 0x7 || npc->race != race) continue;
                if ((npc->GetSex() == RE::SEX::kFemale) != female) continue;
                if (npc->baseTemplateForm || npc->IsDeleted() || SlotIndex(npc) >= 0) continue;
                const auto& flags = npc->actorData.actorBaseFlags;
                if (flags.any(Flag::kIsGhost, Flag::kSummonable, Flag::kInvulnerable, Flag::kSimpleActor, Flag::kDoesntBleed,
                              Flag::kIsChargenFacePreset, Flag::kUsesTemplate)) continue;
                // No head of its own, no name, or a plugin's script on it: not an ordinary person to copy.
                if (npc->numHeadParts < 3 || !npc->GetName() || !*npc->GetName()) continue;
                // Bethesda's debug actors, who live in the test cells (2026-11-23: an Argonian cousin was copied from
                // "Warehouse Traps Reset Test Guy"). The editor ID is there only when a mod such as po3 Tweaks keeps it.
                if (IsTestActor(npc->GetName()) || IsTestActor(npc->GetFormEditorID())) continue;
                const auto id = npc->GetFormID();
                int score = 0;
                if ((id >> 24) == 0x00) score += 2;                            // the base game
                if (npc->npcClass && npc->npcClass->GetFormID() == kCitizenClass) score += 3;
                if (!npc->IsUnique()) score += 2;
                if (npc->defaultOutfit) score += 1;
                if (!flags.any(Flag::kEssential, Flag::kProtected)) score += 1;
                if (npc->GetAggressionLevel() >= RE::ACTOR_AGGRESSION::kVeryAggressive) score -= 8;  // someone's enemy: the last choice
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
            // Peaceful: a Snow Elf copied from a mod's hostile one attacked the inn (2026-11-22). It still defends itself.
            npc->SetAggressionLevel(RE::ACTOR_AGGRESSION::kUnaggressive);
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

    namespace {
        std::string Hex(const RE::Color& c) { return std::format("#{:02X}{:02X}{:02X}", c.red, c.green, c.blue); }
        std::optional<RE::Color> ParseHex(const std::string& s) {
            if (s.size() != 7 || s[0] != '#') return std::nullopt;
            try {
                const auto v = std::stoul(s.substr(1), nullptr, 16);
                RE::Color c;
                c.red = static_cast<std::uint8_t>(v >> 16);
                c.green = static_cast<std::uint8_t>(v >> 8);
                c.blue = static_cast<std::uint8_t>(v);
                return c;
            } catch (...) { return std::nullopt; }
        }
        // A plugin name and local ID, which stay right when the load order changes.
        json StableId(RE::TESForm* form) {
            auto* file = form ? form->GetFile(0) : nullptr;
            if (!file) return nullptr;
            return std::format("{}|{:06X}", file->GetFilename(), form->GetLocalFormID());
        }
        RE::TESForm* FromStableId(const std::string& id) {
            const auto bar = id.rfind('|');
            auto* data = RE::TESDataHandler::GetSingleton();
            if (bar == std::string::npos || !data) return nullptr;
            try { return data->LookupForm(static_cast<RE::FormID>(std::stoul(id.substr(bar + 1), nullptr, 16)), id.substr(0, bar)); } catch (...) { return nullptr; }
        }
        json FaceDataOf(RE::TESNPC* npc) {
            auto* head = npc->headRelatedData;
            return {{"bodyTint", Hex(npc->bodyTintColor)}, {"weight", npc->weight},
                    {"hairColor", head && head->hairColor ? json(Hex(head->hairColor->color)) : json(nullptr)},
                    {"headTexture", head ? StableId(head->faceDetails) : json(nullptr)},
                    {"voice", StableId(npc->voiceType)}, {"combatStyle", StableId(npc->combatStyle)}};
        }
        // Its own head data, never the template's: npc->Copy shares the pointer.
        void ApplyFaceData(RE::TESNPC* npc, const json& d) {
            auto* old = npc->headRelatedData;
            auto* own = new RE::TESNPC::HeadRelatedData();
            if (old) { own->hairColor = old->hairColor; own->faceDetails = old->faceDetails; }
            npc->headRelatedData = own;
            if (d.contains("headTexture") && d["headTexture"].is_string()) {
                if (auto* t = FromStableId(d["headTexture"].get<std::string>())) own->faceDetails = t->As<RE::BGSTextureSet>();
            }
            if (d.contains("hairColor") && d["hairColor"].is_string()) {
                if (const auto c = ParseHex(d["hairColor"].get<std::string>())) {
                    auto* colors = RE::IFormFactory::GetConcreteFormFactoryByType<RE::BGSColorForm>();
                    if (auto* form = colors ? colors->Create() : nullptr) {
                        form->color = *c;
                        own->hairColor = form;
                    }
                }
            }
            if (d.contains("bodyTint") && d["bodyTint"].is_string()) {
                if (const auto c = ParseHex(d["bodyTint"].get<std::string>())) npc->bodyTintColor = *c;
            }
            if (d.contains("weight") && d["weight"].is_number()) npc->weight = std::clamp(d["weight"].get<float>(), 0.0f, 100.0f);
            if (d.contains("combatStyle") && d["combatStyle"].is_string()) {
                if (auto* c = FromStableId(d["combatStyle"].get<std::string>())) if (auto* style = c->As<RE::TESCombatStyle>()) npc->combatStyle = style;
            }
            if (d.contains("voice") && d["voice"].is_string()) {
                if (auto* v = FromStableId(d["voice"].get<std::string>())) if (auto* type = v->As<RE::BGSVoiceType>()) npc->voiceType = type;
            }
        }
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
        return {{"refId", refId}, {"was", label(old)}, {"voice", label(type)}, {"voiceId", std::format("{:08X}", type->GetFormID())}, {"voiceStableId", StableId(type)}};
    }

    json ReapplySlots() {
        auto* data = RE::TESDataHandler::GetSingleton();
        json result = {{"slots", json::array()}, {"rebuild", json::array()}};
        if (!data || !SlotPluginLoaded()) return result;
        json registry;
        try {
            const auto folder = Helpers::GameDocumentsFolder();
            std::ifstream in(folder / "SkyLinkAI" / "slots.json");
            if (folder.empty() || !in) return result;
            registry = json::parse(in);
        } catch (const std::exception& e) {
            result["problem"] = std::string("slots.json unreadable: ") + e.what();
            return result;
        }
        if (!registry.is_object()) return result;
        for (auto& [key, entry] : registry.items()) {
            int slot = -1;
            try { slot = std::stoi(key); } catch (...) {}
            if (slot < 0 || slot >= kSlotCount || !entry.is_object()) continue;
            auto* form = data->LookupForm(kFirstSlot + slot, kSlotPlugin);
            auto* npc = form ? form->As<RE::TESNPC>() : nullptr;
            if (!npc) continue;
            bool changed = false;
            const bool used = npc->GetName() && SlotDefaultName(slot) != npc->GetName();
            // The game uses an NPC's face file only while its race is its original one, and a slot's record is a Nord's: without
            // this a Breton's head was built from parts, with no skin texture (Steam, 2026-11-22).
            RE::TESRace* race = nullptr;
            if (entry.contains("raceId") && entry["raceId"].is_string()) {
                if (auto* r = FromStableId(entry["raceId"].get<std::string>())) race = r->As<RE::TESRace>();
            }
            if (used) race = npc->race;
            if (race && npc->originalRace != race) {
                npc->originalRace = race;
                changed = true;
            }
            bool colours = false;
            if (used && entry.contains("faceData") && entry["faceData"].is_object()) {
                const auto now = FaceDataOf(npc);
                for (const char* field : { "hairColor", "headTexture", "bodyTint", "voice", "combatStyle" }) {
                    if (entry["faceData"].contains(field) && entry["faceData"][field].is_string() && now[field] != entry["faceData"][field]) {
                        if (!colours) ApplyFaceData(npc, entry["faceData"]);
                        colours = true;
                        // Only another head texture needs a new head. Colours are put on the loaded one: switching a character off and
                        // on at every load is seen, and unsettles whatever a follower mod has it doing.
                        if (std::string_view(field) == "headTexture") changed = true;
                    }
                }
            }
            result["slots"].push_back({{"slot", slot}, {"used", used}, {"changed", changed}, {"colours", colours}});
            if ((changed || colours) && used && entry.contains("refId") && entry["refId"].is_string()) {
                RE::TESForm* r = nullptr;
                try { r = RE::TESForm::LookupByID(Helpers::ParseFormId(entry["refId"].get<std::string>())); } catch (...) {}
                auto* actor = r ? r->As<RE::Actor>() : nullptr;
                if (actor && actor->GetActorBase() == npc && actor->Get3D(false) && !actor->IsDisabled()) {
                    if (changed && !actor->IsPlayerTeammate()) {
                        result["rebuild"].push_back(entry["refId"]);
                    } else {
                        actor->UpdateHairColor();
                        actor->UpdateSkinColor();
                    }
                }
            }
        }
        return result;
    }

    json GetFaceData(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc) return {{"error", "Actor not found: " + refId}};
        return FaceDataOf(npc);
    }

    json SetFaceData(const json& params) {
        const auto refId = params.value("refId", std::string());
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc || ((npc->GetFormID() >> 24) != 0xFF && SlotIndex(npc) < 0)) return {{"error", "Not a spawned NPC: " + refId}};
        ApplyFaceData(npc, params);
        // "Face changed" on the record makes the game build the head from parts instead of loading its face file.
        if (SlotIndex(npc) >= 0) {
            if (params.value("useFaceFile", false)) npc->RemoveChange(RE::TESNPC::ChangeFlags::kFace);
            else npc->AddChange(RE::TESNPC::ChangeFlags::kFace);
        }
        return FaceDataOf(npc);
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
            result["race"] = npc->race ? json(npc->race->GetName()) : json(nullptr);
            result["originalRace"] = npc->originalRace ? json(npc->originalRace->GetName()) : json(nullptr);
            result["isChargenPreset"] = npc->actorData.actorBaseFlags.any(RE::ACTOR_BASE_DATA::Flag::kIsChargenFacePreset);
            result["baseFlags"] = std::format("{:08X}", npc->actorData.actorBaseFlags.underlying());
            result["templateUseFlags"] = std::format("{:04X}", npc->actorData.templateUseFlags.underlying());
            result["tintLayers"] = npc->tintLayers ? json(npc->tintLayers->size()) : json(nullptr);
            result["hasMorphs"] = npc->faceData != nullptr;
            result["overlays"] = npc->HasOverlays();
            result["faceNPC"] = npc->faceNPC ? json(std::format("{:08X}", npc->faceNPC->GetFormID())) : json(nullptr);
            auto* hair = npc->headRelatedData ? npc->headRelatedData->hairColor : nullptr;
            result["hairColor"] = hair ? json(std::format("{:08X} #{:02X}{:02X}{:02X}", hair->GetFormID(), hair->color.red, hair->color.green, hair->color.blue)) : json(nullptr);
            result["voice"] = npc->voiceType && npc->voiceType->GetFormEditorID() ? json(npc->voiceType->GetFormEditorID()) : json(nullptr);
        }
        return result;
    }

    namespace {
        constexpr RE::FormID kFamilyFaction = 0xA00;
        constexpr RE::FormID kMarriageFaction = 0x00019809;  // PotentialMarriageFaction
        // Roles that aren't kin by blood and allow marriage (plugin/family-ranks.json "marryable"): Stepbrother, Stepsister.
        constexpr std::array kMarryableRanks{ 18, 19 };
        bool MarryableRank(int rank) { return std::ranges::find(kMarryableRanks, rank) != kMarryableRanks.end(); }

        RE::TESFaction* FamilyFaction() {
            auto* data = RE::TESDataHandler::GetSingleton();
            return data ? data->LookupForm<RE::TESFaction>(kFamilyFaction, kSlotPlugin) : nullptr;
        }
        std::string Lower(std::string s) {
            std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            return s;
        }
        // Rank titles in rank order (the plugin numbers them from 0 without gaps): male title, female title.
        std::vector<std::pair<std::string, std::string>> FamilyTitles(RE::TESFaction* faction) {
            std::vector<std::pair<std::string, std::string>> titles;
            for (auto* rank : faction->rankData) {
                if (rank) titles.emplace_back(rank->maleRankTitle.c_str() ? rank->maleRankTitle.c_str() : "", rank->femaleRankTitle.c_str() ? rank->femaleRankTitle.c_str() : "");
            }
            return titles;
        }
    }

    json FamilyRoles() {
        auto* faction = FamilyFaction();
        if (!faction) return {{"error", "The Player Family faction isn't in the game: SkyLinkAI.esp (the optional NPC plugin) isn't in the load order"}};
        json roles = json::array();
        int rank = 0;
        for (const auto& [male, female] : FamilyTitles(faction)) {
            roles.push_back({{"rank", rank}, {"role", male.empty() ? female : male},
                             {"sex", male.empty() ? json("female") : female.empty() ? json("male") : json(nullptr)}, {"marryable", MarryableRank(rank)}});
            ++rank;
        }
        return {{"faction", std::format("{:08X}", faction->GetFormID())}, {"roles", roles}};
    }

    json SetFamily(const std::string& refId, const std::string& role) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc || ((npc->GetFormID() >> 24) != 0xFF && SlotIndex(npc) < 0)) return {{"error", "Not a spawned NPC: " + refId}};
        auto* faction = FamilyFaction();
        if (!faction) return {{"error", "The Player Family faction isn't in the game: SkyLinkAI.esp (the optional NPC plugin) isn't in the load order"}};
        const bool none = Lower(role) == "none";
        if (!none && npc->race && npc->race->IsChildRace()) return {{"error", "Family characters are adults: this one is a child race"}};

        int rank = -1;
        if (!none) {
            const bool female = npc->GetSex() == RE::SEX::kFemale;
            int i = 0;
            for (const auto& [male, fem] : FamilyTitles(faction)) {
                if (Lower(male) == Lower(role) || Lower(fem) == Lower(role)) {
                    if ((female ? fem : male).empty()) return {{"error", std::format("{} is a {} role and this character is {}", role, female ? "male" : "female", female ? "female" : "male")}};
                    rank = i;
                    break;
                }
                ++i;
            }
            if (rank < 0) return {{"error", "No family role called '" + role + "' (family_roles lists them)"}};
        }
        // A role that allows marriage leaves the character's marriage faction as it is; the others take it away.
        auto* marriage = none || MarryableRank(rank) ? nullptr : RE::TESForm::LookupByID<RE::TESFaction>(kMarriageFaction);
        auto& list = npc->factions;
        for (std::uint32_t i = list.size(); i-- > 0;) {
            if (list[i].faction == faction || (marriage && list[i].faction == marriage)) list.erase(list.begin() + i);
        }
        if (none) {
            actor->RemoveFromFaction(faction);
        } else {
            list.push_back(RE::FACTION_RANK{ faction, static_cast<std::int8_t>(rank) });
            if (marriage) actor->RemoveFromFaction(marriage);
            actor->AddToFaction(faction, static_cast<std::int8_t>(rank));
        }
        if (SlotIndex(npc) >= 0) npc->AddChange(RE::TESNPC::ChangeFlags::kFactions);
        return {{"refId", refId}, {"role", none ? json(nullptr) : json(role)}, {"rank", none ? json(nullptr) : json(rank)},
                {"marryableRole", !none && MarryableRank(rank)}};
    }

    json UseOwnFace(const std::string& refId) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc || SlotIndex(npc) < 0) return {{"error", "Not a character in one of SkyLink's slots: " + refId}};
        npc->faceNPC = nullptr;
        return {{"refId", refId}, {"ownFace", true}};
    }

    // Voices with follower lines (VoicesFollowerAll); without one the recruit line never shows. {has, names of this sex}, or null.
    // The marriage proposal ("Interested in me, are you?") has one line per voice group (Skyrim.esm, RelationshipMarriage):
    // VoicesMarriageNeutral, Arrogant, Rough, Nord, Orc, Argonian, and MaleDrunk alone. Mods that add voices add them to these lists.
    // Null when none of the lists is in the game.
    json VoiceHasMarriageLines(RE::TESNPC* npc) {
        if (!npc || !npc->voiceType) return nullptr;
        bool any = false, has = npc->voiceType->GetFormID() == 0x00013AD4;
        for (RE::FormID id : { 0x000CCD78u, 0x000CCE3Fu, 0x000CCE4Du, 0x000CCE4Eu, 0x000CCE4Fu, 0x000CCE50u }) {
            auto* list = RE::TESForm::LookupByID<RE::BGSListForm>(id);
            if (!list) continue;
            any = true;
            if (list->HasForm(npc->voiceType)) has = true;
        }
        return any ? json(has) : json(nullptr);
    }

    json FollowerVoices(RE::TESNPC* npc) {
        auto* voices = RE::TESForm::LookupByID<RE::BGSListForm>(0x000C78E7);
        if (!voices) return nullptr;
        bool has = false;
        json names = json::array();
        const bool female = npc->GetSex() == RE::SEX::kFemale;
        voices->ForEachForm([&](RE::TESForm* f) {
            if (f == npc->voiceType) has = true;
            if (auto* v = f ? f->As<RE::BGSVoiceType>() : nullptr) {
                const bool isFemale = v->data.flags.any(RE::VOICE_TYPE_DATA::Flag::kFemale);
                if (isFemale == female) names.push_back(v->GetFormEditorID() && v->GetFormEditorID()[0] ? v->GetFormEditorID() : std::format("{:08X}", v->GetFormID()));
            }
            return RE::BSContainer::ForEachResult::kContinue;
        });
        return {{"has", has}, {"names", names}};
    }

    json SetFollower(const std::string& refId, bool follower) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc || ((npc->GetFormID() >> 24) != 0xFF && SlotIndex(npc) < 0)) return {{"error", "Not a spawned NPC: " + refId}};
        if (npc->race && npc->race->IsChildRace()) return {{"error", "Followers are adults: this one is a child race"}};
        auto* potential = RE::TESForm::LookupByID<RE::TESFaction>(0x0005C84D);  // PotentialFollowerFaction, rank 0
        auto* current = RE::TESForm::LookupByID<RE::TESFaction>(0x0005C84E);    // CurrentFollowerFaction, rank -1 until recruited
        if (!potential || !current) return {{"error", "The game's follower factions weren't found"}};

        // Someone following already belongs to the follower dialogue or a follower mod: their rank there is not SkyLink's to reset.
        if (follower && actor->IsPlayerTeammate()) return {{"error", "This character is already following the player"}};
        auto& list = npc->factions;
        for (std::uint32_t i = list.size(); i-- > 0;) if (list[i].faction == potential || list[i].faction == current) list.erase(list.begin() + i);
        if (!follower) {
            if (actor->IsPlayerTeammate()) return {{"error", "This character is following the player: dismiss them first"}};
            actor->RemoveFromFaction(potential);
            actor->RemoveFromFaction(current);
        } else {
            list.push_back(RE::FACTION_RANK{ potential, 0 });
            list.push_back(RE::FACTION_RANK{ current, -1 });
            actor->AddToFaction(potential, 0);
            actor->AddToFaction(current, -1);
            // As the game's own followers: fights the player's enemies, holds its ground, helps friends, grows with the player.
            npc->SetAggressionLevel(RE::ACTOR_AGGRESSION::kAggressive);
            npc->SetConfidenceLevel(RE::ACTOR_CONFIDENCE::kBrave);
            npc->SetAssistanceLevel(RE::ACTOR_ASSISTANCE::kHelpsFriends);
            auto& data = npc->actorData;
            if (!data.actorBaseFlags.any(RE::ACTOR_BASE_DATA::Flag::kEssential)) data.actorBaseFlags.set(RE::ACTOR_BASE_DATA::Flag::kProtected);
            data.actorBaseFlags.set(RE::ACTOR_BASE_DATA::Flag::kPCLevelMult, RE::ACTOR_BASE_DATA::Flag::kAutoCalcStats);
            data.level = 1000;  // the player's level times 1.0
            data.calcLevelMin = 5;
            data.calcLevelMax = 0;
        }
        if (SlotIndex(npc) >= 0) {
            using CF = RE::TESNPC::ChangeFlags;
            npc->AddChange(CF::kFactions | CF::kAIData | CF::kBaseData);
        }

        json result = {{"refId", refId}, {"follower", follower}};
        if (auto voices = FollowerVoices(npc); voices.is_object()) {
            result["voiceHasFollowerLines"] = voices["has"];
            result["voice"] = npc->voiceType && npc->voiceType->GetFormEditorID() ? json(npc->voiceType->GetFormEditorID()) : json(nullptr);
            result["followerVoices"] = voices["names"];
        }
        return result;
    }

    json SetHome(const std::string& refId, const std::string& atRefId) {
        RE::TESForm* form = nullptr;
        RE::TESForm* atForm = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        try { atForm = RE::TESForm::LookupByID(Helpers::ParseFormId(atRefId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        auto* at = atForm ? atForm->As<RE::TESObjectREFR>() : nullptr;
        if (!npc || ((npc->GetFormID() >> 24) != 0xFF && SlotIndex(npc) < 0)) return {{"error", "Not a spawned NPC: " + refId}};
        if (!at) return {{"error", "Not a reference: " + atRefId}};
        if (actor->IsPlayerTeammate()) return {{"error", "This character is following the player: dismiss them first, then give them a home"}};
        auto* location = at->GetCurrentLocation();
        actor->ForceEditorLocation(location);
        if (auto* start = actor->extraList.GetByType<RE::ExtraStartingPosition>()) {
            start->startPosition.pos = at->GetPosition();
            start->location = location;
        }
        actor->MoveTo(at);
        MakeStayPut(npc);  // sandboxes where they stand: at home
        if (SlotIndex(npc) >= 0) npc->AddChange(RE::TESNPC::ChangeFlags::kAIData | RE::TESNPC::ChangeFlags::kFactions);
        actor->EvaluatePackage(true, false);
        return {{"refId", refId}, {"home", atRefId}, {"location", location && location->GetName() ? json(location->GetName()) : json(nullptr)},
                {"editorLocation", actor->GetEditorLocation1() && actor->GetEditorLocation1()->GetName() ? json(actor->GetEditorLocation1()->GetName()) : json(nullptr)}};
    }

    // Mods that keep whom an NPC is attracted to (FEAT-306). Each may be missing; only those loaded are reported.
    // ORomance: StorageUtil int "or_k_sexu" (0 opposite sex, 1 both, 2 same sex), used only while its global ORSexuality is 1.
    // OSL Aroused (and SexLab Aroused): faction slaGenderPreference, rank 0 men, 1 women, 2 both.
    json AttractionMods() {
        auto* data = RE::TESDataHandler::GetSingleton();
        json mods = json::object();
        if (auto* quest = data ? data->LookupForm<RE::TESQuest>(0x800, "ORomance.esp") : nullptr) {
            auto* on = data->LookupForm<RE::TESGlobal>(0x63A5, "ORomance.esp");
            mods["ORomance"] = {{"quest", std::format("{:08X}", quest->GetFormID())}, {"sexualityOption", on && on->value >= 0.5f}};
        }
        if (auto* faction = data ? data->LookupForm<RE::TESFaction>(0x79A72, "SexLabAroused.esm") : nullptr)
            mods["OSL Aroused"] = {{"faction", std::format("{:08X}", faction->GetFormID())}};
        return {{"mods", mods}};
    }

    json SetAttraction(const std::string& refId, const std::string& attraction) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor || actor->IsPlayerRef()) return {{"error", "Not an NPC: " + refId}};
        const bool women = attraction == "women", men = attraction == "men", everyone = attraction == "everyone";
        if (!women && !men && !everyone) return {{"error", "attraction must be women, men or everyone"}};
        auto* npc = actor->GetActorBase();
        const bool female = npc && npc->GetSex() == RE::SEX::kFemale;
        json result = {{"refId", refId}, {"attraction", attraction}};
        auto mods = AttractionMods()["mods"];
        if (mods.contains("OSL Aroused")) {
            auto* faction = RE::TESForm::LookupByID<RE::TESFaction>(Helpers::ParseFormId(mods["OSL Aroused"]["faction"].get<std::string>()));
            const std::int8_t rank = everyone ? 2 : women ? 1 : 0;
            actor->AddToFaction(faction, rank);  // as Papyrus SetFactionRank: joins, or changes the rank
            result["OSL Aroused"] = {{"genderPreference", rank}};
        }
        // ORomance's number is relative to the NPC's sex; the C# side stores it through ORomance's own script.
        if (mods.contains("ORomance")) {
            auto o = mods["ORomance"];
            o["value"] = everyone ? 1 : (women == female) ? 2 : 0;
            result["ORomance"] = o;
        }
        return result;
    }

    json HomeMarkers(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        auto* cell = actor ? actor->GetParentCell() : nullptr;
        if (!cell) return {{"error", "Actor not found or in no cell: " + refId}};
        // An alias can only be given a reference that always exists (persistent), and one every player has (the base game's).
        static constexpr std::string_view kBase[] = { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        struct Found { RE::TESObjectREFR* ref; float distance; };
        std::vector<Found> found;
        cell->ForEachReference([&](RE::TESObjectREFR* ref) {
            auto* base = ref ? ref->GetBaseObject() : nullptr;
            auto* file = ref ? ref->GetFile(0) : nullptr;
            if (!base || !file || ref->IsDisabled() || ref->Is(RE::FormType::ActorCharacter)) return RE::BSContainer::ForEachResult::kContinue;
            if (!(ref->GetFormFlags() & RE::TESForm::RecordFlags::kPersistent)) return RE::BSContainer::ForEachResult::kContinue;
            if (std::find(std::begin(kBase), std::end(kBase), file->GetFilename()) == std::end(kBase)) return RE::BSContainer::ForEachResult::kContinue;
            found.push_back({ ref, ref->GetPosition().GetDistance(actor->GetPosition()) });
            return RE::BSContainer::ForEachResult::kContinue;
        });
        std::sort(found.begin(), found.end(), [](const Found& a, const Found& b) { return a.distance < b.distance; });
        json markers = json::array();
        for (const auto& f : found) {
            auto* base = f.ref->GetBaseObject();
            const bool marker = base->GetFormID() == 0x3B || base->GetFormID() == 0x34;  // XMarker, XMarkerHeading
            markers.push_back({{"stableId", StableId(f.ref)}, {"distance", f.distance}, {"marker", marker},
                               {"what", marker ? json("marker") : json(base->GetName() && base->GetName()[0] ? base->GetName() : RE::FormTypeToString(base->GetFormType()).data())}});
            if (markers.size() >= 25) break;
        }
        return {{"cell", cell->GetName()}, {"interior", cell->IsInteriorCell()}, {"markers", markers}};
    }

    json CharacterForms(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        if (!npc) return {{"error", "Actor not found: " + refId}};
        json forms = json::array();
        // kind: what it is; need: "record" (written into an exported NPC), "carried" (in the inventory, maybe put there by a distributor).
        auto add = [&](RE::TESForm* form, const char* kind, const char* need, const char* model = nullptr) {
            if (!form) return;
            auto* file = form->GetFile(0);
            if (!file) return;  // made at run time: nothing to depend on
            json f = {{"formId", std::format("{:08X}", form->GetFormID())}, {"kind", kind}, {"need", need}, {"plugin", std::string(file->GetFilename())},
                      {"stableId", StableId(form)}, {"name", form->GetName() && form->GetName()[0] ? json(form->GetName()) : json(nullptr)}};
            if (const char* e = form->GetFormEditorID(); e && e[0]) f["editorId"] = e;
            if (model && model[0]) f["model"] = model;
            // A record another plugin changes: the look may come from the last one.
            if (auto* files = form->sourceFiles.array; files && files->size() > 1) f["lastChangedBy"] = std::string(files->back()->GetFilename());
            forms.push_back(f);
        };
        add(npc->race, "race", "record");
        add(npc->voiceType, "voice", "record");
        add(npc->npcClass, "class", "record");
        add(npc->combatStyle, "combat style", "record");
        add(npc->skin, "skin", "record");
        add(npc->defaultOutfit, "outfit", "record");
        add(npc->sleepOutfit, "sleep outfit", "record");
        if (auto* head = npc->headRelatedData) {
            add(head->hairColor, "hair colour", "record");
            add(head->faceDetails, "head texture set", "record");
        }
        for (int i = 0; npc->headParts && i < npc->numHeadParts; ++i) {
            if (auto* part = npc->headParts[i]) {
                add(part, "head part", "record", part->GetModel());
                add(part->textureSet, "head part textures", "record");
            }
        }
        for (const auto& f : npc->factions) add(f.faction, "faction", "record");
        for (auto* package : npc->aiPackages.packages) add(package, "package", "record");
        if (auto* spells = npc->GetSpellList()) {
            for (std::uint32_t i = 0; spells->spells && i < spells->numSpells; ++i) add(spells->spells[i], "spell", "record");
        }
        for (std::uint32_t i = 0; npc->perks && i < npc->perkCount; ++i) add(npc->perks[i].perk, "perk", "record");
        for (auto& [item, entry] : actor->GetInventory()) {
            if (!item || entry.first <= 0) continue;
            const bool worn = entry.second && entry.second->IsWorn();
            add(item, worn ? "worn" : "carried", worn ? "worn" : "carried");
        }
        return {{"refId", refId}, {"name", npc->GetName()}, {"sex", npc->GetSex() == RE::SEX::kFemale ? "female" : "male"}, {"forms", forms},
                {"faceSource", FaceSource(npc)}, {"slot", SlotIndex(npc) >= 0 ? json(SlotIndex(npc)) : json(nullptr)},
                {"faceData", FaceDataOf(npc)}, {"height", npc->GetHeight()}, {"level", actor->GetLevel()},
                {"voiceHasFollowerLines", [&] { auto v = FollowerVoices(npc); return v.is_object() ? v["has"] : json(nullptr); }()},
                {"voiceHasMarriageLines", VoiceHasMarriageLines(npc)},
                {"essential", npc->actorData.actorBaseFlags.any(RE::ACTOR_BASE_DATA::Flag::kEssential)},
                {"protected", npc->actorData.actorBaseFlags.any(RE::ACTOR_BASE_DATA::Flag::kProtected)}};
    }

    json CopyOutfit(const std::string& refId) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* actor = form ? form->As<RE::Actor>() : nullptr;
        auto* npc = actor ? actor->GetActorBase() : nullptr;
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* equip = RE::ActorEquipManager::GetSingleton();
        if (!player || !equip) return {{"error", "Player not available"}};
        if (!npc || ((npc->GetFormID() >> 24) != 0xFF && SlotIndex(npc) < 0)) return {{"error", "Not a spawned NPC: " + refId}};

        std::vector<RE::TESObjectARMO*> worn;
        for (auto& [item, entry] : player->GetInventory([](RE::TESBoundObject& o) { return o.Is(RE::FormType::Armor); })) {
            if (entry.second && entry.second->IsWorn()) if (auto* armor = item->As<RE::TESObjectARMO>()) worn.push_back(armor);
        }
        for (auto* outfit : { npc->defaultOutfit, npc->sleepOutfit }) if (outfit) actor->RemoveOutfitItems(outfit);
        npc->defaultOutfit = nullptr;
        npc->sleepOutfit = nullptr;
        if (SlotIndex(npc) >= 0) npc->AddChange(RE::TESNPC::ChangeFlags::kDefaultOutfit | RE::TESNPC::ChangeFlags::kSleepOutfit);

        // What it has on already (a distributor such as SPID may have dressed it) comes off; it keeps those items.
        for (auto& [item, entry] : actor->GetInventory([](RE::TESBoundObject& o) { return o.Is(RE::FormType::Armor); })) {
            if (entry.second && entry.second->IsWorn()) equip->UnequipObject(actor, item, nullptr, 1, nullptr, true, true, false);
        }
        json items = json::array();
        for (auto* armor : worn) {
            actor->AddObjectToContainer(armor, nullptr, 1, nullptr);
            // Not forced: a forced item can't be taken off again by trading or by a follower mod's outfits.
            equip->EquipObject(actor, armor, nullptr, 1, nullptr, true, false, false);
            items.push_back(armor->GetName() ? armor->GetName() : "");
        }
        return {{"refId", refId}, {"items", items}, {"count", worn.size()}};
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
        std::vector<int> prefer;
        if (params.contains("preferSlots") && params["preferSlots"].is_array())
            for (const auto& a : params["preferSlots"]) if (a.is_number_integer()) prefer.push_back(a.get<int>());
        RE::TESNPC* npc = params.value("useSlot", true) ? FreeSlot(race, female, slotIndex, avoid, prefer) : nullptr;
        if (!npc) {
            auto* factory = RE::IFormFactory::GetConcreteFormFactoryByType<RE::TESNPC>();
            npc = factory ? factory->Create() : nullptr;
        }
        if (!npc) return {{"error", "The game's NPC form factory didn't create a form"}};
        npc->Copy(tmpl);
        // The template's belongings: its keys open its house, not the copy's (2026-11-22: a copy of Tova Shatter-Shield had the Key to
        // Hjerim), and a slot's own container isn't saved. Ordinary things are given to the character itself; keys and leveled lists aren't.
        std::vector<std::pair<RE::TESBoundObject*, std::int32_t>> belongings;
        tmpl->ForEachContainerObject([&](RE::ContainerObject& entry) {
            if (entry.obj && entry.count > 0 && entry.obj->GetPlayable() &&
                (entry.obj->Is(RE::FormType::Weapon) || entry.obj->Is(RE::FormType::Armor) || entry.obj->Is(RE::FormType::Ammo))) belongings.emplace_back(entry.obj, entry.count);
            return RE::BSContainer::ForEachResult::kContinue;
        });
        npc->containerObjects = nullptr;
        npc->numContainerObjects = 0;
        npc->race = race;
        npc->originalRace = race;  // its face file is used only while the two match
        if (!params.value("keepSchedule", false)) MakeStayPut(npc);
        // Marryable unless asked otherwise (family members aren't); never a child. The game's marriage talk also needs a voice that has it.
        bool marryable = params.value("marryable", true) && race && !race->IsChildRace();
        if (auto* faction = marryable ? RE::TESForm::LookupByID<RE::TESFaction>(0x00019809) : nullptr) {  // PotentialMarriageFaction
            npc->factions.push_back(RE::FACTION_RANK{ faction, 0 });
        } else {
            marryable = false;
        }

        const auto name = params.value("name", std::string());
        if (!name.empty()) npc->SetFullName(name.c_str());
        // A copy of a unique NPC would share its "only one of me" rule. A slot has one character, so it is unique in its own right:
        // follower mods keep per-follower settings only for unique NPCs (Nether's Follower Framework asks IsUnique before it does).
        if (slotIndex >= 0) npc->actorData.actorBaseFlags.set(RE::ACTOR_BASE_DATA::Flag::kUnique);
        else npc->actorData.actorBaseFlags.reset(RE::ACTOR_BASE_DATA::Flag::kUnique);
        if (params.value("essential", false)) npc->actorData.actorBaseFlags.set(RE::ACTOR_BASE_DATA::Flag::kEssential);

        // The face: the template's own FaceGen (it has a file), or none, for RaceMenu's CharGen to build one.
        const auto face = params.value("face", std::string("template"));
        npc->faceNPC = face == "template" ? (tmpl->faceNPC ? tmpl->faceNPC : tmpl) : nullptr;

        // Spike 2026-11-22 (Steam): CharGen.LoadCharacter copies head parts and morphs but not the hair colour, body tone, weight
        // or head texture set, and the template's texture set replaces the face file's own textures, its tint included (a white
        // face with the template's wrinkles). So take the player's; never write through the head data the template still owns.
        const bool sharedHead = npc->headRelatedData && npc->headRelatedData == tmpl->headRelatedData;
        if (face == "player") {
            auto data = FaceDataOf(playerBase);
            data.erase("voice");
            data.erase("combatStyle");
            ApplyFaceData(npc, data);
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
        for (const auto& [object, count] : belongings) ref->AddObjectToContainer(object, nullptr, count, nullptr);
        const float placedAt = PlaceInFront(ref.get(), player, std::clamp(params.value("distance", 160.0f), 60.0f, 600.0f));

        return {
            {"refId", std::format("{:08X}", ref->GetFormID())},
            {"baseId", std::format("{:08X}", npc->GetFormID())},
            {"name", npc->GetName()},
            {"race", std::format("{:08X}", race->GetFormID())},
            {"raceName", race->GetName()},
            {"raceId", StableId(race)},
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
            {"marryable", marryable},
            {"faceData", FaceDataOf(npc)},
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
        if (auto* follower = ref->As<RE::Actor>(); follower && follower->IsPlayerTeammate())
            return {{"error", "This character is following the player. Dismiss them first (a follower mod would keep an empty place for them)."}};
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

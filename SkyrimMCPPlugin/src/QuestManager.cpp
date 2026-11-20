#include "QuestManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <format>

namespace SkyrimMCP::QuestManager {

    using json = nlohmann::json;

    json GetQuestInfo() {
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};

        json quests = json::array();

        for (auto* quest : dataHandler->GetFormArray<RE::TESQuest>()) {
            try {
                if (!quest || !quest->IsActive()) continue;

                json q;
                q["formId"] = std::format("{:08X}", quest->GetFormID());
                q["name"] = quest->GetName() ? quest->GetName() : "";
                q["currentStage"] = quest->GetCurrentStageID();
                q["completed"] = quest->IsCompleted();

                json objectives = json::array();
                for (auto& obj : quest->objectives) {
                    if (obj && obj->initialized) {
                        json o;
                        o["index"] = obj->index;
                        o["text"] = obj->displayText.c_str() ? obj->displayText.c_str() : "";
                        o["state"] = static_cast<int>(obj->state.get());
                        objectives.push_back(o);
                    }
                }
                q["objectives"] = objectives;
                quests.push_back(q);
            } catch (...) {
                // Skip quests with bad data (common with modded games)
                continue;
            }
        }

        return {{"quests", quests}};
    }

    json GetQuestStages(const std::string& formIdHex) {
        try {
            auto* quest = RE::TESForm::LookupByID<RE::TESQuest>(Helpers::ParseFormId(formIdHex));
            if (!quest) return {{"error", "Quest not found: " + formIdHex}};

            json result;
            result["formId"] = formIdHex;
            result["name"] = quest->GetName() ? quest->GetName() : "";
            result["currentStage"] = quest->GetCurrentStageID();

            // Executed stages (stages that have been completed)
            json executed = json::array();
            if (quest->executedStages) {
                for (auto& stage : *quest->executedStages) {
                    json s;
                    s["index"] = stage.data.index;
                    s["isStartup"] = stage.data.flags.all(RE::QUEST_STAGE_DATA::Flag::kStartUpStage);
                    s["isShutdown"] = stage.data.flags.all(RE::QUEST_STAGE_DATA::Flag::kShutDownStage);
                    executed.push_back(s);
                }
            }
            result["executedStages"] = executed;

            // Waiting stages (stages queued but not yet processed)
            json waiting = json::array();
            if (quest->waitingStages) {
                for (auto* stage : *quest->waitingStages) {
                    if (!stage) continue;
                    json s;
                    s["index"] = stage->data.index;
                    waiting.push_back(s);
                }
            }
            result["waitingStages"] = waiting;
            // FEAT-263: these in-memory lists don't say which stages are done (a replayed quest at 100 still listed 15, 20 and
            // 100 as waiting; executedStages read only zeros). The save's quest data is the record: ReplayQuests and the
            // blueprint export read it from there.
            result["note"] = "executedStages and waitingStages aren't a record of stages done; stages done come from a save (GetCharacterBlueprint, ReplayQuests).";

            // Objectives
            json objectives = json::array();
            for (auto& obj : quest->objectives) {
                if (!obj || !obj->initialized) continue;
                json o;
                o["index"] = obj->index;
                o["text"] = obj->displayText.c_str() ? obj->displayText.c_str() : "";
                o["state"] = static_cast<int>(obj->state.get());
                objectives.push_back(o);
            }
            result["objectives"] = objectives;

            return result;
        } catch (...) {
            return {{"error", "Failed to get stages: " + formIdHex}};
        }
    }

    json GetQuestAliases(const std::string& formIdHex) {
        try {
            auto* quest = RE::TESForm::LookupByID<RE::TESQuest>(Helpers::ParseFormId(formIdHex));
            if (!quest) return {{"error", "Quest not found: " + formIdHex}};

            json result;
            result["formId"] = formIdHex;
            result["name"] = quest->GetName() ? quest->GetName() : "";

            json aliases = json::array();
            for (auto* alias : quest->aliases) {
                if (!alias) continue;
                try {
                    json a;
                    a["id"] = alias->aliasID;
                    a["name"] = alias->aliasName.c_str() ? alias->aliasName.c_str() : "";
                    a["isQuestObject"] = alias->IsQuestObject();
                    a["isEssential"] = alias->IsEssential();
                    a["isProtected"] = alias->IsProtected();
                    // FEAT-263: ReplayQuests stops a started quest whose required aliases stayed empty (a story-manager quest
                    // such as a Jarl's bounty, started cold, fills none, and its stages then do nothing).
                    a["optional"] = alias->flags.any(RE::BGSBaseAlias::FLAGS::kOptional);

                    // Resolve alias to a reference
                    // Use VMTypeID check instead of dynamic_cast (RTTI can fail in SKSE plugins)
                    if (alias->GetVMTypeID() == RE::BGSRefAlias::VMTYPEID) {
                        auto* refAlias = static_cast<RE::BGSRefAlias*>(alias);
                        a["type"] = "reference";
                        auto* ref = refAlias->GetReference();
                        a["filled"] = ref != nullptr;
                        if (ref) {
                            a["refId"] = std::format("{:08X}", ref->GetFormID());
                            a["refName"] = ref->GetName() ? ref->GetName() : "";
                            auto* baseObj = ref->GetBaseObject();
                            if (baseObj) a["baseId"] = std::format("{:08X}", baseObj->GetFormID());

                            auto* actor = ref->As<RE::Actor>();
                            if (actor) {
                                a["isActor"] = true;
                                a["isDead"] = actor->IsDead();
                                a["level"] = actor->GetLevel();
                            }
                        } else {
                            a["refId"] = "none";
                            a["note"] = "Alias is empty — no NPC/object filling this slot";
                        }
                    } else {
                        a["type"] = "other";
                        a["vmTypeId"] = alias->GetVMTypeID();
                    }

                    aliases.push_back(a);
                } catch (...) {
                    continue;
                }
            }
            result["aliases"] = aliases;
            result["count"] = aliases.size();

            return result;
        } catch (...) {
            return {{"error", "Failed to get aliases: " + formIdHex}};
        }
    }

    json GetQuestItems() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto inv = player->GetInventory();
        json questItems = json::array();

        for (auto& [form, data] : inv) {
            if (!form || data.first <= 0) continue;
            try {
                auto& entryData = data.second;
                if (entryData && entryData->IsQuestObject()) {
                    json item;
                    item["formId"] = std::format("{:08X}", form->GetFormID());
                    item["name"] = form->GetName();
                    item["count"] = data.first;

                    const char* displayName = entryData->GetDisplayName();
                    if (displayName && displayName[0] && std::strcmp(displayName, form->GetName()) != 0) {
                        item["displayName"] = displayName;
                    }

                    questItems.push_back(item);
                }
            } catch (...) {
                continue;
            }
        }

        return {{"questItems", questItems}, {"count", questItems.size()}};
    }

    json GetQuestStage(const std::string& formIdHex) {
        try {
            auto* quest = RE::TESForm::LookupByID<RE::TESQuest>(Helpers::ParseFormId(formIdHex));
            if (!quest) return {{"error", "Quest not found: " + formIdHex}};

            json result;
            result["formId"] = formIdHex;
            result["name"] = quest->GetName();
            result["currentStage"] = quest->GetCurrentStageID();
            result["isActive"] = quest->IsActive();
            result["isCompleted"] = quest->IsCompleted();
            result["isRunning"] = quest->IsRunning();

            json objectives = json::array();
            for (auto& obj : quest->objectives) {
                if (obj && obj->initialized) {
                    objectives.push_back({
                        {"index", obj->index},
                        {"text", obj->displayText.c_str()},
                        {"state", static_cast<int>(obj->state.get())}
                    });
                }
            }
            result["objectives"] = objectives;
            return result;
        } catch (...) {
            return {{"error", "Invalid FormID: " + formIdHex}};
        }
    }

    // No native SetStage API in CommonLibSSE-NG — console command is the only option
    json SetQuestStage(const std::string& formIdHex, int stage) {
        return Helpers::ExecuteConsoleCommand(std::format("setstage {} {}", formIdHex, stage));
    }

    json CompleteQuest(const std::string& formIdHex) {
        try {
            auto* quest = RE::TESForm::LookupByID<RE::TESQuest>(Helpers::ParseFormId(formIdHex));
            if (!quest) return {{"error", "Quest not found: " + formIdHex}};

            int completed = 0;
            for (auto& obj : quest->objectives) {
                if (obj && obj->initialized) {
                    obj->state = RE::QUEST_OBJECTIVE_STATE::kCompleted;
                    completed++;
                }
            }

            json result;
            result["completed"] = true;
            result["formId"] = formIdHex;
            result["name"] = quest->GetName() ? quest->GetName() : "";
            result["objectivesCompleted"] = completed;
            return result;
        } catch (...) {
            return {{"error", "Failed to complete quest: " + formIdHex}};
        }
    }

    json StartQuest(const std::string& formIdHex) {
        try {
            auto* quest = RE::TESForm::LookupByID<RE::TESQuest>(Helpers::ParseFormId(formIdHex));
            if (!quest) return {{"error", "Quest not found: " + formIdHex}};

            bool started = quest->Start();
            return {{"started", started}, {"formId", formIdHex}, {"name", quest->GetName() ? quest->GetName() : ""}};
        } catch (...) {
            return {{"error", "Failed to start quest: " + formIdHex}};
        }
    }

    json StopQuest(const std::string& formIdHex) {
        try {
            auto* quest = RE::TESForm::LookupByID<RE::TESQuest>(Helpers::ParseFormId(formIdHex));
            if (!quest) return {{"error", "Quest not found: " + formIdHex}};

            quest->Stop();
            return {{"stopped", true}, {"formId", formIdHex}, {"name", quest->GetName() ? quest->GetName() : ""}};
        } catch (...) {
            return {{"error", "Failed to stop quest: " + formIdHex}};
        }
    }

}

#pragma once

// GameInterface.h — Facade header for backward compatibility with Protocol.cpp
// All functions are now in domain-specific modules under namespace SkyrimMCP.
// This header re-exports them under SkyrimMCP::GameInterface so existing callers
// (Protocol.cpp) continue to work without modification.

#include "Helpers.h"
#include "CombatAnalysis.h"
#include "PlayerQueries.h"
#include "InventoryManager.h"
#include "QuestManager.h"
#include "NPCManager.h"
#include "WorldManager.h"
#include "PapyrusBridge.h"
#include "UIManager.h"
#include "UtilityManager.h"
#include "AppearanceManager.h"

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // All functions MUST be called on the game thread (via TaskQueue)
    namespace GameInterface {

        // === Helpers / Console ===
        inline json ExecuteConsoleCommand(const std::string& command) { return Helpers::ExecuteConsoleCommand(command); }
        inline json GetGameSafety() { return Helpers::GetGameSafetyJson(); }

        // === Combat (CombatAnalysis) ===
        inline json GetCombatState() { return CombatAnalysis::GetCombatState(); }
        inline json GetDamageStats() { return CombatAnalysis::GetDamageStats(); }
        inline json GetThreats(float radius) { return CombatAnalysis::GetThreats(radius); }

        // === Player State (PlayerQueries) ===
        inline json GetPlayerInfo() { return PlayerQueries::GetPlayerInfo(); }
        inline json GetInventory(const std::string& refId = "") { return PlayerQueries::GetInventory(refId); }
        inline json GetGoldCount() { return PlayerQueries::GetGoldCount(); }
        inline json GetActiveEffects() { return PlayerQueries::GetActiveEffects(); }
        inline json GetEquippedItems() { return PlayerQueries::GetEquippedItems(); }
        inline json GetKnownSpells() { return PlayerQueries::GetKnownSpells(); }
        inline json GetKnownShouts() { return PlayerQueries::GetKnownShouts(); }
        inline json GetSkillLevels() { return PlayerQueries::GetSkillLevels(); }
        inline json GetPerks() { return PlayerQueries::GetPerks(); }
        inline json GetAppearance(const std::string& refId = "") { return PlayerQueries::GetAppearance(refId); }
        inline json GetFavorites() { return PlayerQueries::GetFavorites(); }
        inline json GetCharacterBlueprint() { return PlayerQueries::GetCharacterBlueprint(); }
        inline json IsInCombat() { return PlayerQueries::IsInCombat(); }
        inline json GetMagicResistances() { return PlayerQueries::GetMagicResistances(); }
        inline json GetPowers() { return PlayerQueries::GetPowers(); }
        inline json SetLevel(int level) { return PlayerQueries::SetLevel(level); }
        inline json ToggleGodMode() { return PlayerQueries::ToggleGodMode(); }
        inline json ToggleImmortalMode() { return PlayerQueries::ToggleImmortalMode(); }
        inline json GetDiseaseStatus() { return PlayerQueries::GetDiseaseStatus(); }
        inline json GetEnchantmentInfo(const std::string& formIdHex) { return PlayerQueries::GetEnchantmentInfo(formIdHex); }
        inline json GetKnownEnchantments() { return PlayerQueries::GetKnownEnchantments(); }
        inline json GetSpellDetails(const std::string& formIdHex) { return PlayerQueries::GetSpellDetails(formIdHex); }

        // === Inventory (InventoryManager) ===
        inline json AddItem(const std::string& formIdHex, int count) { return InventoryManager::AddItem(formIdHex, count); }
        inline json RemoveItem(const std::string& formIdHex, int count) { return InventoryManager::RemoveItem(formIdHex, count); }
        inline json EquipItem(const std::string& formIdHex) { return InventoryManager::EquipItem(formIdHex); }
        inline json UnequipItem(const std::string& formIdHex) { return InventoryManager::UnequipItem(formIdHex); }
        inline json EquipItems(const json& items, const std::string& refId, bool unequip) { return InventoryManager::EquipItems(items, refId, unequip); }

        // === Appearance (AppearanceManager) ===
        inline json GetColors(const std::string& refId) { return AppearanceManager::GetColors(refId); }
        inline json ListHairColors(bool includeNonPlayable) { return AppearanceManager::ListHairColors(includeNonPlayable); }
        inline json SetHairColor(const std::string& refId, const std::string& color, bool exact = false) { return AppearanceManager::SetHairColor(refId, color, exact); }
        inline json SetSkinTone(const std::string& refId, const std::string& color, bool refresh) { return AppearanceManager::SetSkinTone(refId, color, refresh); }

        // === Quests (QuestManager) ===
        inline json GetQuestInfo() { return QuestManager::GetQuestInfo(); }
        inline json GetQuestStage(const std::string& formIdHex) { return QuestManager::GetQuestStage(formIdHex); }
        inline json GetQuestStages(const std::string& formIdHex) { return QuestManager::GetQuestStages(formIdHex); }
        inline json SetQuestStage(const std::string& formIdHex, int stage) { return QuestManager::SetQuestStage(formIdHex, stage); }
        inline json CompleteQuest(const std::string& formIdHex) { return QuestManager::CompleteQuest(formIdHex); }
        inline json StartQuest(const std::string& formIdHex) { return QuestManager::StartQuest(formIdHex); }
        inline json StopQuest(const std::string& formIdHex) { return QuestManager::StopQuest(formIdHex); }
        inline json GetQuestAliases(const std::string& formIdHex) { return QuestManager::GetQuestAliases(formIdHex); }
        inline json GetQuestItems() { return QuestManager::GetQuestItems(); }

        // === Spells / Perks (still use console commands via Helpers) ===
        inline json AddSpell(const std::string& formIdHex) {
            try {
                auto* spell = RE::TESForm::LookupByID<RE::SpellItem>(Helpers::ParseFormId(formIdHex));
                if (!spell) return {{"error", "Spell not found: " + formIdHex}};
                auto* player = RE::PlayerCharacter::GetSingleton();
                if (!player) return {{"error", "Player not available"}};
                player->AddSpell(spell);
                return {{"added", true}, {"formId", formIdHex}, {"name", spell->GetName()}};
            } catch (...) {
                return {{"error", "Invalid FormID: " + formIdHex}};
            }
        }
        inline json RemoveSpell(const std::string& formIdHex) {
            try {
                auto* spell = RE::TESForm::LookupByID<RE::SpellItem>(Helpers::ParseFormId(formIdHex));
                if (!spell) return {{"error", "Spell not found: " + formIdHex}};
                auto* player = RE::PlayerCharacter::GetSingleton();
                if (!player) return {{"error", "Player not available"}};
                bool removed = player->RemoveSpell(spell);
                return {{"removed", removed}, {"formId", formIdHex}, {"name", spell->GetName()}};
            } catch (...) {
                return {{"error", "Invalid FormID: " + formIdHex}};
            }
        }
        inline json AddPerk(const std::string& formIdHex) {
            try {
                auto* perk = RE::TESForm::LookupByID<RE::BGSPerk>(Helpers::ParseFormId(formIdHex));
                if (!perk) return {{"error", "Perk not found: " + formIdHex}};
                auto* player = RE::PlayerCharacter::GetSingleton();
                if (!player) return {{"error", "Player not available"}};
                player->AddPerk(perk);
                return {{"added", true}, {"formId", formIdHex}, {"name", perk->GetName()}};
            } catch (...) {
                return {{"error", "Invalid FormID: " + formIdHex}};
            }
        }
        inline json RemovePerk(const std::string& formIdHex) {
            try {
                auto* perk = RE::TESForm::LookupByID<RE::BGSPerk>(Helpers::ParseFormId(formIdHex));
                if (!perk) return {{"error", "Perk not found: " + formIdHex}};
                auto* player = RE::PlayerCharacter::GetSingleton();
                if (!player) return {{"error", "Player not available"}};
                player->RemovePerk(perk);
                return {{"removed", true}, {"formId", formIdHex}, {"name", perk->GetName()}};
            } catch (...) {
                return {{"error", "Invalid FormID: " + formIdHex}};
            }
        }
        inline json UnlockShout(const std::string& formIdHex) {
            try {
                auto formId = Helpers::ParseFormId(formIdHex);
                auto* form = RE::TESForm::LookupByID(formId);
                if (!form) return {{"error", "Form not found: " + formIdHex}};

                auto* player = RE::PlayerCharacter::GetSingleton();
                if (!player) return {{"error", "Player not available"}};

                auto* shout = form->As<RE::TESShout>();
                if (shout) {
                    player->AddShout(shout);
                    for (int i = 0; i < 3; i++) {
                        if (shout->variations[i].word) {
                            player->UnlockWord(shout->variations[i].word);
                        }
                    }
                    return {{"unlocked", true}, {"formId", formIdHex}, {"name", shout->GetName()}, {"type", "shout"}};
                }

                auto* word = form->As<RE::TESWordOfPower>();
                if (word) {
                    player->UnlockWord(word);
                    // A word alone left the shout out of the magic menu (2026-11-20): add the shout it belongs to if she lacks it.
                    json shoutAdded = nullptr;
                    if (auto* dh = RE::TESDataHandler::GetSingleton()) {
                        for (auto* sh : dh->GetFormArray<RE::TESShout>()) {
                            if (!sh) continue;
                            bool has = false;
                            for (int i = 0; i < 3; i++) if (sh->variations[i].word == word) has = true;
                            if (!has) continue;
                            bool known = false;
                            if (auto* list = player->GetActorBase() ? player->GetActorBase()->GetSpellList() : nullptr)
                                for (std::uint32_t i = 0; i < list->numShouts; ++i) if (list->shouts[i] == sh) known = true;
                            if (!known && player->AddShout(sh)) shoutAdded = sh->GetName();
                            break;
                        }
                    }
                    return {{"unlocked", true}, {"formId", formIdHex}, {"name", word->GetName()}, {"type", "word"}, {"shoutAdded", shoutAdded}};
                }

                return {{"error", "Not a shout or word of power: " + formIdHex}};
            } catch (...) {
                return {{"error", "Failed to unlock: " + formIdHex}};
            }
        }

        // === NPC / Actor (NPCManager) ===
        inline json GetNearbyNPCs(float radius) { return NPCManager::GetNearbyNPCs(radius); }
        inline json GetActorInfo(const std::string& formIdHex) { return NPCManager::GetActorInfo(formIdHex); }
        inline json GetNPCDetailedInfo(const std::string& refFormIdHex) { return NPCManager::GetNPCDetailedInfo(refFormIdHex); }
        inline json GetFollowers() { return NPCManager::GetFollowers(); }
        inline json GetDetectionLevel(const std::string& refFormIdHex) { return NPCManager::GetDetectionLevel(refFormIdHex); }
        inline json MoveActorTo(const std::string& actorFormIdHex, const std::string& targetCellOrRefId) { return NPCManager::MoveActorTo(actorFormIdHex, targetCellOrRefId); }
        inline json SetRelationshipRank(const std::string& actorFormIdHex, int rank) { return NPCManager::SetRelationshipRank(actorFormIdHex, rank); }
        inline json KillActor(const std::string& refFormIdHex) { return NPCManager::KillActor(refFormIdHex); }
        inline json GetCrosshairRef() { return NPCManager::GetCrosshairRef(); }
        inline json SetActorValue(const std::string& attribute, float value, const std::string& refId = "") { return NPCManager::SetActorValue(attribute, value, refId); }
        inline json GetFactions(const std::string& refId = "") { return NPCManager::GetFactions(refId); }
        inline json PlayIdle(const std::string& idleFormIdHex, const std::string& refId = "") { return NPCManager::PlayIdle(idleFormIdHex, refId); }
        inline json GetCurrentIdle(const std::string& refId = "") { return NPCManager::GetCurrentIdle(refId); }
        inline json StopIdle(const std::string& refId = "") { return NPCManager::StopIdle(refId); }

        // === World (WorldManager) ===
        inline json Teleport(const std::string& cellId, const std::string& method = "auto") { return WorldManager::Teleport(cellId, method); }
        inline json GetWeather() { return WorldManager::GetWeather(); }
        inline json SetWeather(const std::string& weatherFormIdOrName) { return WorldManager::SetWeather(weatherFormIdOrName); }
        inline json ListWeathers() { return WorldManager::ListWeathers(); }
        inline json GetCellInfo() { return WorldManager::GetCellInfo(); }
        inline json GetNearbyObjects(float radius, const std::string& typeFilter) { return WorldManager::GetNearbyObjects(radius, typeFilter); }
        inline json UnlockDoor(const std::string& refFormIdHex) { return WorldManager::UnlockDoor(refFormIdHex); }
        inline json LockDoor(const std::string& refFormIdHex, int lockLevel) { return WorldManager::LockDoor(refFormIdHex, lockLevel); }
        inline json GetContainerInventory(const std::string& refFormIdHex) { return WorldManager::GetContainerInventory(refFormIdHex); }
        inline json DiscoverAllMapMarkers() { return WorldManager::DiscoverAllMapMarkers(); }
        inline json GetGameTime() { return WorldManager::GetGameTime(); }
        inline json SetGameTime(float hours) { return WorldManager::SetGameTime(hours); }
        inline json ToggleCollision(const std::string& refFormIdHex = "") { return WorldManager::ToggleCollision(refFormIdHex); }
        inline json GetNearbyMerchants(float radius) { return WorldManager::GetNearbyMerchants(radius); }
        inline json GetMerchantInventory(const std::string& refFormIdHex) { return WorldManager::GetMerchantInventory(refFormIdHex); }
        inline json GetBounties() { return WorldManager::GetBounties(); }
        inline json ClearBounty(const std::string& factionFormIdHex) { return WorldManager::ClearBounty(factionFormIdHex); }
        inline json PlayMusic(const std::string& formIdHex) { return WorldManager::PlayMusic(formIdHex); }
        inline json StopMusic() { return WorldManager::StopMusic(); }

        // === Papyrus Bridge ===
        inline json GetPapyrusCatalog() { return PapyrusBridge::GetPapyrusCatalog(); }
        inline json ScanPapyrusSources() { return PapyrusBridge::ScanPapyrusSources(); }
        inline json GetScriptFunctions(const std::string& className) { return PapyrusBridge::GetScriptFunctions(className); }
        inline json CallPapyrusFunction(const std::string& className, const std::string& functionName, const json& args, const std::string& target = "") { return PapyrusBridge::CallPapyrusFunction(className, functionName, args, target); }

        // === UI (UIManager) ===
        inline json GetMenuState() { return UIManager::GetMenuState(); }

        // === Utility (UtilityManager) ===
        inline json ShowNotification(const std::string& message) { return UtilityManager::ShowNotification(message); }
        inline json SearchForms(const std::string& query, const std::string& formTypeFilter, int maxResults) { return UtilityManager::SearchForms(query, formTypeFilter, maxResults); }
        inline json GetLoadedSKSEPlugins() { return UtilityManager::GetLoadedSKSEPlugins(); }
        inline json SaveGame(const std::string& saveName) { return UtilityManager::SaveGame(saveName); }
        inline json GetLoadOrder() { return UtilityManager::GetLoadOrder(); }
        inline json GetModFormIdPrefix(const std::string& modName) { return UtilityManager::GetModFormIdPrefix(modName); }
        inline json LoadSave(const std::string& saveName) { return UtilityManager::LoadSave(saveName); }
        inline json LoadMostRecentSave() { return UtilityManager::LoadMostRecentSave(); }
    }

}

#include "Protocol.h"
#include "GameInterface.h"
#include "TaskQueue.h"
#include "LootManager.h"
#include "AppearanceManager.h"
#include "FaceCamera.h"
#include "Studio.h"
#include "FaceState.h"
#include "ScriptStats.h"
#include "BlueprintManager.h"
#include "UtilityManager.h"

#include <SKSE/SKSE.h>

#include <chrono>
#include <filesystem>
#include <functional>
#include <unordered_map>

namespace SkyrimMCP::Protocol {

    using Handler = std::function<std::string(const std::string& id, const json& params)>;

    static json MakeResponse(const std::string& id, bool success, const json& data, const std::string& error = "") {
        json response;
        response["id"] = id;
        response["success"] = success;
        if (success) {
            response["data"] = data;
        } else {
            response["error"] = error;
        }
        return response;
    }

    // Helper: wrap a no-arg game thread call
    static std::string GameThread(const std::string& id, std::function<json()> fn) {
        auto result = TaskQueue::RunOnGameThread(std::move(fn));
        if (result.contains("error")) {
            return MakeResponse(id, false, {}, result["error"].get<std::string>()).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        }
        return MakeResponse(id, true, result).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
    }

    // Helper: require a string param, then run on game thread
    static std::string RequireStr(const std::string& id, const json& params,
        const char* paramName, std::function<json(const std::string&)> fn) {
        std::string val = params.value(paramName, "");
        if (val.empty()) return MakeResponse(id, false, {}, std::string("Missing '") + paramName + "' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        return GameThread(id, [fn, val]() { return fn(val); });
    }

    static std::unordered_map<std::string, Handler> BuildRegistry() {
        std::unordered_map<std::string, Handler> registry;

        // === Direct handlers (no game thread) ===

        // ping and poll_events are answered by the host DLL (HostCore.cpp): they must
        // work while this DLL is being reloaded, and the event sinks live there.

        registry["game_thread_ping"] = [](const std::string& id, const json& params) {
            // Round trip to the game thread and back: shows whether game-thread calls
            // are being serviced right now (they stall during loading screens).
            int timeoutMs = params.value("timeoutMs", 5000);
            auto started = std::chrono::steady_clock::now();
            TaskQueue::RunOnGameThread([]() { return json::object(); }, timeoutMs);
            auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - started).count();
            return MakeResponse(id, true, {{"gameThreadMs", ms}}).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };

        registry["save_game"] = [](const std::string& id, const json& params) {
            std::string saveName = params.value("saveName", "MCPSave");
            TaskQueue::Post([saveName]() {
                GameInterface::SaveGame(saveName);
            });
            return MakeResponse(id, true, {{"saving", true}, {"saveName", saveName}}).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };

        // === No-param game thread handlers ===

        auto noParam = [&](const char* name, std::function<json()> fn) {
            registry[name] = [fn](const std::string& id, const json&) {
                return GameThread(id, fn);
            };
        };

        noParam("get_player_info", []() { return GameInterface::GetPlayerInfo(); });

        registry["get_inventory"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            return GameThread(id, [refId]() { return GameInterface::GetInventory(refId); });
        };
        noParam("get_gold_count", []() { return GameInterface::GetGoldCount(); });
        noParam("get_active_effects", []() { return GameInterface::GetActiveEffects(); });
        noParam("get_equipped_items", []() { return GameInterface::GetEquippedItems(); });
        noParam("get_known_spells", []() { return GameInterface::GetKnownSpells(); });
        noParam("get_known_shouts", []() { return GameInterface::GetKnownShouts(); });
        noParam("get_known_enchantments", []() { return GameInterface::GetKnownEnchantments(); });
        noParam("get_skill_levels", []() { return GameInterface::GetSkillLevels(); });
        noParam("get_perks", []() { return GameInterface::GetPerks(); });
        // Which player controls a script has switched off (mouse look dead in game but fine in menus after a blueprint apply,
        // 2026-11-20: a script disabled looking and never gave it back).
        noParam("get_controls", []() -> json {
            auto* map = RE::ControlMap::GetSingleton();
            if (!map) return {{"error", "Control map not available"}};
            using F = RE::UserEvents::USER_EVENT_FLAG;
            static const std::pair<const char*, F> kControls[] = {
                {"movement", F::kMovement}, {"looking", F::kLooking}, {"activate", F::kActivate}, {"menu", F::kMenu},
                {"console", F::kConsole}, {"pov", F::kPOVSwitch}, {"fighting", F::kFighting}, {"sneaking", F::kSneaking},
                {"mainFour", F::kMainFour}, {"wheelZoom", F::kWheelZoom}, {"jumping", F::kJumping},
            };
            json off = json::array();
            for (const auto& [name, flag] : kControls) if (!map->AreControlsEnabled(flag)) off.push_back(name);
            return {{"disabled", off}};
        });
        // BUG-091 follow-up: a drawn weapon or spell blocks saving in some load orders; SaveGame sheathes first.
        noParam("sheathe", []() -> json {
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* state = player ? player->AsActorState() : nullptr;
            if (!state) return {{"error", "No player"}};
            const bool drawn = state->IsWeaponDrawn();
            if (drawn && state->GetWeaponState() == RE::WEAPON_STATE::kDrawn) player->DrawWeaponMagicHands(false);
            return {{"wasDrawn", drawn}, {"weaponState", static_cast<int>(state->GetWeaponState())}};
        });
        // BUG-091: put a lost character ID back (RepairCharacterId); only when the game has none, never over a real one.
        registry["set_character_id"] = [](const std::string& id, const json& params) {
            const auto hex = params.value("characterId", std::string());
            if (hex.size() != 8 || !std::all_of(hex.begin(), hex.end(), [](unsigned char c) { return std::isxdigit(c) != 0; }))
                return MakeResponse(id, false, {}, "characterId must be 8 hex digits").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            const auto value = static_cast<std::uint32_t>(std::stoul(hex, nullptr, 16));
            return GameThread(id, [value]() -> json {
                if (!Helpers::CharacterIdLost()) return {{"error", "The game has a character ID (or no game is loaded): nothing to repair."}};
                RE::BGSSaveLoadManager::GetSingleton()->currentCharacterID = value;
                return {{"characterId", std::format("{:08X}", value)}, {"repaired", true}};
            });
        };
        registry["get_appearance"] = [](const std::string& id, const json& params) {
            const auto refId = params.value("refId", std::string());
            return GameThread(id, [refId]() { return GameInterface::GetAppearance(refId); });
        };
        noParam("get_favorites", []() { return GameInterface::GetFavorites(); });
        noParam("get_character_blueprint", []() { return GameInterface::GetCharacterBlueprint(); });
        noParam("is_in_combat", []() { return GameInterface::IsInCombat(); });
        noParam("get_quest_info", []() { return GameInterface::GetQuestInfo(); });
        noParam("get_quest_items", []() { return GameInterface::GetQuestItems(); });
        noParam("toggle_god_mode", []() { return GameInterface::ToggleGodMode(); });
        registry["toggle_collision"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            return GameThread(id, [refId]() { return GameInterface::ToggleCollision(refId); });
        };
        noParam("get_weather", []() { return GameInterface::GetWeather(); });
        noParam("list_weathers", []() { return GameInterface::ListWeathers(); });
        noParam("get_cell_info", []() { return GameInterface::GetCellInfo(); });
        noParam("get_game_time", []() { return GameInterface::GetGameTime(); });
        noParam("get_crosshair_ref", []() { return GameInterface::GetCrosshairRef(); });
        noParam("get_load_order", []() { return GameInterface::GetLoadOrder(); });
        noParam("get_loaded_skse_plugins", []() { return GameInterface::GetLoadedSKSEPlugins(); });
        noParam("discover_all_map_markers", []() { return GameInterface::DiscoverAllMapMarkers(); });
        registry["get_factions"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            return GameThread(id, [refId]() { return GameInterface::GetFactions(refId); });
        };
        registry["get_player_factions"] = registry["get_factions"];  // backward compat alias
        noParam("get_bounties", []() { return GameInterface::GetBounties(); });
        noParam("get_magic_resistances", []() { return GameInterface::GetMagicResistances(); });
        noParam("get_disease_status", []() { return GameInterface::GetDiseaseStatus(); });
        noParam("get_powers", []() { return GameInterface::GetPowers(); });
        noParam("get_followers", []() { return GameInterface::GetFollowers(); });
        noParam("toggle_immortal_mode", []() { return GameInterface::ToggleImmortalMode(); });
        noParam("get_combat_state", []() { return GameInterface::GetCombatState(); });
        noParam("get_menu_state", []() { return GameInterface::GetMenuState(); });
        noParam("get_game_safety", []() { return GameInterface::GetGameSafety(); });
        noParam("get_session_state", []() { return UtilityManager::GetSessionState(); });
        noParam("quit_game", []() { return UtilityManager::QuitGame(); });
        noParam("reload_content", []() { return UtilityManager::ReloadContent(); });
        noParam("load_most_recent_save", []() { return GameInterface::LoadMostRecentSave(); });

        // Papyrus Bridge — these run on pipe thread (no game thread needed for catalog)
        registry["get_papyrus_catalog"] = [](const std::string& id, const json&) {
            // Catalog scan is file I/O, safe on any thread
            auto result = GameInterface::GetPapyrusCatalog();
            if (result.contains("error"))
                return MakeResponse(id, false, {}, result["error"].get<std::string>()).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return MakeResponse(id, true, result).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
        registry["scan_papyrus_sources"] = [](const std::string& id, const json&) {
            auto result = GameInterface::ScanPapyrusSources();
            if (result.contains("error"))
                return MakeResponse(id, false, {}, result["error"].get<std::string>()).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return MakeResponse(id, true, result).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
        // get_script_functions is registered below with other formIdParam entries
        registry["call_papyrus_function"] = [](const std::string& id, const json& params) {
            std::string className = params.value("className", "");
            std::string functionName = params.value("functionName", "");
            auto args = params.value("args", json::array());
            if (className.empty() || functionName.empty())
                return MakeResponse(id, false, {}, "Missing className or functionName").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            // DO NOT use GameThread — DispatchStaticCall queues onto the VM which
            // executes on the game thread. If we block the game thread waiting for
            // the result, we deadlock. Call directly from the pipe thread instead.
            std::string target = params.value("target", "");
            auto result = GameInterface::CallPapyrusFunction(className, functionName, args, target, params.value("wait", true));
            if (result.contains("error"))
                return MakeResponse(id, false, {}, result["error"].get<std::string>()).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return MakeResponse(id, true, result).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
        noParam("get_damage_stats", []() { return GameInterface::GetDamageStats(); });

        registry["set_level"] = [](const std::string& id, const json& params) {
            int level = params.value("level", 1);
            return GameThread(id, [level]() { return GameInterface::SetLevel(level); });
        };

        registry["get_threats"] = [](const std::string& id, const json& params) {
            float radius = params.value("radius", 4096.0f);
            return GameThread(id, [radius]() { return GameInterface::GetThreats(radius); });
        };

        registry["get_nearby_merchants"] = [](const std::string& id, const json& params) {
            float radius = params.value("radius", 4096.0f);
            return GameThread(id, [radius]() { return GameInterface::GetNearbyMerchants(radius); });
        };

        // === Single formId/refId param handlers ===

        auto formIdParam = [&](const char* name, const char* paramName, std::function<json(const std::string&)> fn) {
            registry[name] = [paramName, fn](const std::string& id, const json& params) {
                return RequireStr(id, params, paramName, fn);
            };
        };

        formIdParam("get_quest_stage", "formId", [](const std::string& f) { return GameInterface::GetQuestStage(f); });
        registry["get_journal"] = [](const std::string& id, const json& params) {
            std::string quest = params.value("quest", "");
            int maxQuests = params.value("maxQuests", 60);
            bool includeHidden = params.value("includeHidden", false);
            return GameThread(id, [quest, maxQuests, includeHidden]() { return QuestManager::GetJournal(quest, maxQuests, includeHidden); });
        };
        formIdParam("get_quest_stages", "formId", [](const std::string& f) { return GameInterface::GetQuestStages(f); });
        formIdParam("get_quest_aliases", "formId", [](const std::string& f) { return GameInterface::GetQuestAliases(f); });
        formIdParam("complete_quest", "formId", [](const std::string& f) { return GameInterface::CompleteQuest(f); });
        formIdParam("start_quest", "formId", [](const std::string& f) { return GameInterface::StartQuest(f); });
        formIdParam("stop_quest", "formId", [](const std::string& f) { return GameInterface::StopQuest(f); });
        formIdParam("add_spell", "formId", [](const std::string& f) { return GameInterface::AddSpell(f); });
        formIdParam("remove_spell", "formId", [](const std::string& f) { return GameInterface::RemoveSpell(f); });
        formIdParam("add_perk", "formId", [](const std::string& f) { return GameInterface::AddPerk(f); });
        formIdParam("remove_perk", "formId", [](const std::string& f) { return GameInterface::RemovePerk(f); });
        formIdParam("get_actor_info", "formId", [](const std::string& f) { return GameInterface::GetActorInfo(f); });
        formIdParam("equip_item", "formId", [](const std::string& f) { return GameInterface::EquipItem(f); });
        formIdParam("unequip_item", "formId", [](const std::string& f) { return GameInterface::UnequipItem(f); });
        formIdParam("unlock_shout", "formId", [](const std::string& f) { return GameInterface::UnlockShout(f); });
        formIdParam("unlock_door", "refId", [](const std::string& r) { return GameInterface::UnlockDoor(r); });
        formIdParam("kill_actor", "refId", [](const std::string& r) { return GameInterface::KillActor(r); });
        registry["spawn_npc"] = [](const std::string& id, const json& params) {
            return GameThread(id, [params]() { return NPCManager::SpawnNpc(params); });
        };
        registry["set_spawned_npc_enabled"] = [](const std::string& id, const json& params) {
            std::string ref = params.value("refId", std::string());
            bool enable = params.value("enable", true);
            return GameThread(id, [ref, enable]() { return NPCManager::SetSpawnedNpcEnabled(ref, enable); });
        };
        formIdParam("spawned_npc_stay", "refId", [](const std::string& r) { return NPCManager::SpawnedNpcStay(r); });
        formIdParam("remove_spawned_npc", "refId", [](const std::string& r) { return NPCManager::RemoveSpawnedNpc(r); });
        registry["play_idle"] = [](const std::string& id, const json& params) {
            std::string idleFormId = params.value("idleFormId", "");
            std::string refId = params.value("refId", "");
            if (idleFormId.empty())
                return MakeResponse(id, false, {}, "Missing idleFormId").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [idleFormId, refId]() { return GameInterface::PlayIdle(idleFormId, refId); });
        };
        registry["get_current_idle"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            return GameThread(id, [refId]() { return GameInterface::GetCurrentIdle(refId); });
        };
        registry["stop_idle"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            return GameThread(id, [refId]() { return GameInterface::StopIdle(refId); });
        };

        formIdParam("get_npc_detailed_info", "refId", [](const std::string& r) { return GameInterface::GetNPCDetailedInfo(r); });
        formIdParam("get_npc_inventory", "refId", [](const std::string& r) { return GameInterface::GetInventory(r); });
        formIdParam("get_container_inventory", "refId", [](const std::string& r) { return GameInterface::GetContainerInventory(r); });
        formIdParam("get_detection_level", "refId", [](const std::string& r) { return GameInterface::GetDetectionLevel(r); });
        formIdParam("get_mod_formid_prefix", "modName", [](const std::string& m) { return GameInterface::GetModFormIdPrefix(m); });
        formIdParam("set_weather", "weather", [](const std::string& w) { return GameInterface::SetWeather(w); });
        formIdParam("load_save", "saveName", [](const std::string& s) { return GameInterface::LoadSave(s); });
        formIdParam("get_merchant_inventory", "refId", [](const std::string& r) { return GameInterface::GetMerchantInventory(r); });
        formIdParam("clear_bounty", "factionFormId", [](const std::string& f) { return GameInterface::ClearBounty(f); });
        formIdParam("play_music", "formId", [](const std::string& f) { return GameInterface::PlayMusic(f); });
        noParam("stop_music", []() { return GameInterface::StopMusic(); });
        formIdParam("get_spell_details", "formId", [](const std::string& f) { return GameInterface::GetSpellDetails(f); });
        formIdParam("get_enchantment_info", "formId", [](const std::string& f) { return GameInterface::GetEnchantmentInfo(f); });
        formIdParam("get_script_functions", "className", [](const std::string& c) { return GameInterface::GetScriptFunctions(c); });

        // === Multi-param handlers ===

        registry["execute_command"] = [](const std::string& id, const json& params) {
            std::string command = params.value("command", "");
            if (command.empty()) return MakeResponse(id, false, {}, "Missing 'command' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [command]() { return GameInterface::ExecuteConsoleCommand(command); });
        };

        // FEAT-218: move items by category
        registry["transfer_items"] = [](const std::string& id, const json& params) {
            return GameThread(id, [params]() { return LootManager::TransferItems(params); });
        };
        registry["loot_bodies"] = [](const std::string& id, const json& params) {
            return GameThread(id, [params]() { return LootManager::LootBodies(params); });
        };

        // FEAT-207: add missing items, equip and restore gold from a blueprint
        // BUG-088: the server keeps per-game state in the running game's own documents folder.
        // BUG-024: console moves (coc, cow, player.moveto) run the loading screen inside the call and time out though they work;
        // start them on the next frame and let the server wait for the load.
        registry["execute_command_async"] = [](const std::string& id, const json& params) {
            const auto command = params.value("command", std::string());
            if (command.empty()) return MakeResponse(id, false, {}, "Missing 'command' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            if (const auto blocked = Helpers::MoveBlocker(); !blocked.empty()) {
                return MakeResponse(id, false, {}, blocked).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            }
            TaskQueue::Post([command]() { Helpers::ExecuteConsoleCommand(command); });
            return MakeResponse(id, true, {{"started", command}}).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
                registry["get_game_folder"] = [](const std::string& id, const json&) {
            const auto folder = Helpers::GameDocumentsFolder();
            const auto u8 = folder.u8string();
            std::string install;
            try { const auto c = std::filesystem::current_path().u8string(); install.assign(c.begin(), c.end()); } catch (...) {}
            return MakeResponse(id, true, {{"folder", std::string(u8.begin(), u8.end())}, {"gameFolder", install}}).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
                registry["export_quests"] = [](const std::string& id, const json&) {
            return GameThread(id, []() {
                auto q = PlayerQueries::ExportQuests();
                BlueprintManager::AddPortableIds(q);
                return q;
            });
        };
                registry["apply_blueprint_items"] = [](const std::string& id, const json& params) {
            return GameThread(id, [params]() { return BlueprintManager::ApplyBlueprintItems(params); });
        };

        // FEAT-131/133: native volume and screenshot (the console versions did nothing)
        registry["set_volume"] = [](const std::string& id, const json& params) {
            std::string category = params.value("category", "master");
            float volume = params.value("volume", 1.0f);
            return GameThread(id, [=]() { return UtilityManager::SetVolume(category, volume); });
        };
        registry["take_screenshot"] = [](const std::string& id, const json&) {
            return GameThread(id, []() { return UtilityManager::TakeScreenshot(); });
        };

        // FEAT-150: Papyrus VM diagnostics
        registry["get_script_stats"] = [](const std::string& id, const json& params) {
            int top = params.value("top", 15);
            return GameThread(id, [top]() { return ScriptStats::GetScriptStats(top); });
        };

        // FEAT-280: the objects behind a runaway handler, for StopRunaway
        registry["find_stack_objects"] = [](const std::string& id, const json& params) {
            std::string entry = params.value("entry", "");
            int limit = params.value("limit", 100);
            return GameThread(id, [entry, limit]() { return ScriptStats::FindStackObjects(entry, limit); });
        };

        registry["unregister_updates"] = [](const std::string& id, const json& params) {
            std::vector<std::uint64_t> handles;
            for (const auto& h : params.value("handles", json::array())) {
                try { handles.push_back(std::stoull(h.get<std::string>(), nullptr, 16)); } catch (...) {}
            }
            bool gameTime = params.value("gameTime", false);
            return GameThread(id, [handles, gameTime]() { return ScriptStats::UnregisterUpdates(handles, gameTime); });
        };

        // FEAT-226: hair colour and skin tone with the game's own data (no RaceMenu needed)
        registry["get_colors"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            return GameThread(id, [refId]() { return GameInterface::GetColors(refId); });
        };
        registry["list_head_part_choices"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            std::string type = params.value("type", "hair");
            std::string order = params.value("order", "");
            int start = params.value("start", 0);
            int count = params.value("count", 21);
            std::string around = params.value("aroundFormId", "");
            bool showExcluded = params.value("showExcluded", false);
            int exStart = params.value("excludedStart", 0);
            int exCount = params.value("excludedCount", 60);
            std::string rule = params.value("excludedRule", "");
            std::string explain = params.value("explain", "");
            return GameThread(id, [=]() {
                if (!explain.empty()) return AppearanceManager::ExcludedHeadParts(refId, type, 0, 1, "", explain);
                auto result = AppearanceManager::ListHeadPartChoices(refId, type, order, start, count, around);
                if (showExcluded && !result.contains("error"))
                    result["excluded"] = AppearanceManager::ExcludedHeadParts(refId, type, exStart, exCount, rule, "");
                return result;
            });
        };
        registry["set_head_part"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            std::string part = params.value("part", "");
            std::string type = params.value("type", "");
            int index = params.value("index", -1);
            bool refresh = params.value("refresh", false);
            return GameThread(id, [=]() { return AppearanceManager::SetHeadPart(refId, part, type, index, refresh); });
        };
        registry["studio"] = [](const std::string& id, const json& params) {
            std::string mode = params.value("mode", "status");
            std::string preset = params.value("preset", "neutral");
            float intensity = params.value("intensity", 1.0f);
            std::string cell = params.value("cell", "");
            // Only return and cleanup take a marker; ParseFormId throws on an empty string (BUG-036).
            const std::string markerText = params.value("markerId", std::string{});
            const auto marker = markerText.empty() ? 0u : static_cast<std::uint32_t>(Helpers::ParseFormId(markerText));
            if (mode == "lights") return GameThread(id, [=]() { return Studio::Lights(preset, intensity); });
            if (mode == "go") return GameThread(id, [=]() { return Studio::Go(cell); });
            if (mode == "return") return GameThread(id, [=]() { return Studio::Return(marker); });
            if (mode == "cleanup") return GameThread(id, [=]() { return Studio::Cleanup(marker); });
            if (mode == "status") return GameThread(id, []() { return Studio::Status(); });
            if (mode == "find") return GameThread(id, []() { return Studio::FindMarkers(); });
            return MakeResponse(id, false, {}, "mode must be lights, go, return, cleanup, find or status").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
        registry["face_camera"] = [](const std::string& id, const json& params) {
            std::string mode = params.value("mode", "");
            bool hideHud = params.value("hideHud", true);
            bool hideHair = params.value("hideHair", false);
            float angle = params.value("angleDeg", 0.0f);
            float distance = params.value("distance", 40.0f);
            float height = params.value("heightOffset", 0.0f);
            bool aimFromHead = params.value("aimFromHead", true);
            int expression = params.value("expression", -1);
            float strength = params.value("strength", 0.7f);
            float mouthOpen = params.value("mouthOpen", 0.0f);
            if (mode == "prepare") return GameThread(id, [=]() { return FaceCamera::Prepare(expression, strength, mouthOpen); });
            bool hideEffects = params.value("hideEffects", true);
            if (mode == "enter") return GameThread(id, [=]() { return FaceCamera::Enter(hideHud, hideHair, hideEffects); });
            const std::string aimRef = params.value("refId", std::string());
            if (mode == "aim") return GameThread(id, [=]() { return FaceCamera::Aim(angle, distance, height, aimFromHead, aimRef); });
            if (mode == "snapshot") return GameThread(id, []() { return FaceCamera::SculptSnapshot(); });
            if (mode == "check") return GameThread(id, []() { return FaceCamera::Check(); });
                        if (mode == "exit") return GameThread(id, []() { return FaceCamera::Exit(); });
            return MakeResponse(id, false, {}, "mode must be prepare, enter, aim, snapshot, check or exit").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
        // FEAT-257: proves the game-thread fault guard (FEAT-220) by faulting on purpose inside a guarded task. The fault is in
        // SkyLink's own code with no engine lock or object held, so after it is caught the game is unaffected.
        registry["crash_test"] = [](const std::string& id, const json& params) {
            if (params.value("confirm", std::string()) != "crash-test") {
                return MakeResponse(id, false, {}, "crash_test needs confirm: \"crash-test\"").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            }
            const auto mode = params.value("mode", std::string("null_read"));
            if (mode != "null_read" && mode != "divide_by_zero") {
                return MakeResponse(id, false, {}, "mode must be null_read or divide_by_zero").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            }
            const auto faultsBefore = TaskQueue::FaultCount();
            json out = {{"mode", mode}, {"faultsCaughtBefore", faultsBefore}};
            try {
                auto r = TaskQueue::RunOnGameThread([mode]() -> json {
                    if (mode == "divide_by_zero") {
                        volatile int zero = 0;
                        volatile int value = 1 / zero;
                        return {{"value", static_cast<int>(value)}};
                    }
                    volatile int* nowhere = nullptr;
                    volatile int value = *nowhere;
                    return {{"value", static_cast<int>(value)}};
                });
                out["caught"] = false;
                out["result"] = "No fault was raised (unexpected): " + r.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace);
            } catch (const std::exception& e) {
                out["caught"] = TaskQueue::FaultCount() > faultsBefore;
                out["guardMessage"] = e.what();
            }
            out["faultsCaughtNow"] = TaskQueue::FaultCount();
            out["note"] = "The fault was in SkyLink's own code with no engine lock or object held, so the game is unaffected; "
                          "the real guard message tells players to save to a new slot because a real fault may leave the engine "
                          "in a bad state.";
            return MakeResponse(id, true, out).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        };
        registry["reapply_face_state"] = [](const std::string& id, const json&) {
            return GameThread(id, []() { return FaceState::Reapply(); });
        };
        registry["head_part_tint"] = [](const std::string& id, const json& params) {
            int type = params.value("type", 6);
            std::string color = params.value("color", "");
            bool fromHair = params.value("fromHairColor", false);
            return GameThread(id, [=]() { return AppearanceManager::HeadPartTint(type, color, fromHair); });
        };
        registry["restore_head_parts"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            std::vector<std::string> parts;
            if (params.contains("parts") && params["parts"].is_array()) {
                for (const auto& p : params["parts"]) if (p.is_string()) parts.push_back(p.get<std::string>());
            }
            bool refresh = params.value("refresh", true);
            return GameThread(id, [=]() { return AppearanceManager::RestoreHeadParts(refId, parts, refresh); });
        };
        registry["remove_head_part"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            std::string part = params.value("part", "");
            std::string type = params.value("type", "");
            bool refresh = params.value("refresh", false);
            return GameThread(id, [=]() { return AppearanceManager::RemoveHeadPart(refId, part, type, refresh); });
        };
        registry["list_hair_colors"] = [](const std::string& id, const json& params) {
            bool all = params.value("includeNonPlayable", false);
            return GameThread(id, [all]() { return GameInterface::ListHairColors(all); });
        };
        registry["set_hair_color"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            std::string color = params.value("color", "");
            if (color.empty()) return MakeResponse(id, false, {}, "Missing 'color' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            bool exact = params.value("exact", false);
            return GameThread(id, [refId, color, exact]() { return GameInterface::SetHairColor(refId, color, exact); });
        };
        registry["set_skin_tone"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            std::string color = params.value("color", "");
            bool refresh = params.value("refresh", false);
            if (color.empty()) return MakeResponse(id, false, {}, "Missing 'color' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [refId, color, refresh]() { return GameInterface::SetSkinTone(refId, color, refresh); });
        };

        // FEAT-205: {"items": ["0001397E", {"formId": "...", "hand": "left"}], "refId": "", "unequip": false}
        registry["equip_items"] = [](const std::string& id, const json& params) {
            json items = params.value("items", json::array());
            std::string refId = params.value("refId", "");
            bool unequip = params.value("unequip", false);
            return GameThread(id, [items, refId, unequip]() { return GameInterface::EquipItems(items, refId, unequip); });
        };

        registry["add_item"] = [](const std::string& id, const json& params) {
            std::string formId = params.value("formId", "");
            int count = params.value("count", 1);
            if (formId.empty()) return MakeResponse(id, false, {}, "Missing 'formId' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [formId, count]() { return GameInterface::AddItem(formId, count); });
        };

        registry["remove_item"] = [](const std::string& id, const json& params) {
            std::string formId = params.value("formId", "");
            int count = params.value("count", 1);
            if (formId.empty()) return MakeResponse(id, false, {}, "Missing 'formId' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [formId, count]() { return GameInterface::RemoveItem(formId, count); });
        };

        registry["set_actor_value"] = [](const std::string& id, const json& params) {
            std::string attribute = params.value("attribute", "");
            float value = params.value("value", 0.0f);
            std::string refId = params.value("refId", "");
            if (attribute.empty()) return MakeResponse(id, false, {}, "Missing 'attribute' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [attribute, value, refId]() { return GameInterface::SetActorValue(attribute, value, refId); });
        };

        registry["teleport"] = [](const std::string& id, const json& params) {
            std::string cellId = params.value("cellId", "");
            if (cellId.empty()) return MakeResponse(id, false, {}, "Missing 'cellId' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            const std::string method = params.value("method", std::string("auto"));
            return GameThread(id, [cellId, method]() { return GameInterface::Teleport(cellId, method); });
        };

        registry["set_quest_stage"] = [](const std::string& id, const json& params) {
            std::string formId = params.value("formId", "");
            int stage = params.value("stage", 0);
            if (formId.empty()) return MakeResponse(id, false, {}, "Missing 'formId' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [formId, stage]() { return GameInterface::SetQuestStage(formId, stage); });
        };

        registry["set_actor_value_on"] = [](const std::string& id, const json& params) {
            std::string actorId = params.value("actorFormId", "");
            std::string attribute = params.value("attribute", "");
            float value = params.value("value", 0.0f);
            if (actorId.empty() || attribute.empty()) return MakeResponse(id, false, {}, "Missing parameters").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [actorId, attribute, value]() { return GameInterface::SetActorValue(attribute, value, actorId); });
        };

        registry["move_actor_to"] = [](const std::string& id, const json& params) {
            std::string actorId = params.value("actorFormId", "");
            std::string target = params.value("target", "");
            if (actorId.empty()) return MakeResponse(id, false, {}, "Missing 'actorFormId' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [actorId, target]() { return GameInterface::MoveActorTo(actorId, target); });
        };

        registry["set_relationship_rank"] = [](const std::string& id, const json& params) {
            std::string actorId = params.value("actorFormId", "");
            int rank = params.value("rank", 0);
            if (actorId.empty()) return MakeResponse(id, false, {}, "Missing 'actorFormId' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [actorId, rank]() { return GameInterface::SetRelationshipRank(actorId, rank); });
        };

        registry["lock_door"] = [](const std::string& id, const json& params) {
            std::string refId = params.value("refId", "");
            int lockLevel = params.value("lockLevel", 1);
            if (refId.empty()) return MakeResponse(id, false, {}, "Missing 'refId' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [refId, lockLevel]() { return GameInterface::LockDoor(refId, lockLevel); });
        };

        registry["get_nearby_npcs"] = [](const std::string& id, const json& params) {
            float radius = params.value("radius", 4096.0f);
            return GameThread(id, [radius]() { return GameInterface::GetNearbyNPCs(radius); });
        };

        registry["get_nearby_objects"] = [](const std::string& id, const json& params) {
            float radius = params.value("radius", 2048.0f);
            std::string typeFilter = params.value("type", "all");
            return GameThread(id, [radius, typeFilter]() { return GameInterface::GetNearbyObjects(radius, typeFilter); });
        };

        registry["set_game_time"] = [](const std::string& id, const json& params) {
            float hours = params.value("hours", 12.0f);
            return GameThread(id, [hours]() { return GameInterface::SetGameTime(hours); });
        };

        registry["show_notification"] = [](const std::string& id, const json& params) {
            std::string message = params.value("message", "");
            if (message.empty()) return MakeResponse(id, false, {}, "Missing 'message' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [message]() { return GameInterface::ShowNotification(message); });
        };

        registry["search_forms"] = [](const std::string& id, const json& params) {
            std::string query = params.value("query", "");
            std::string typeFilter = params.value("type", "all");
            int maxResults = params.value("maxResults", 25);
            if (query.empty()) return MakeResponse(id, false, {}, "Missing 'query' parameter").dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            return GameThread(id, [query, typeFilter, maxResults]() { return GameInterface::SearchForms(query, typeFilter, maxResults); });
        };

        return registry;
    }

    // Built once, thread-safely (a function-local static). The old "static bool
    // initialized" version let a second thread see a half-built registry: "Unknown
    // action" or "bad function call" for the first concurrent requests after a load.
    static std::unordered_map<std::string, Handler>& GetRegistry() {
        static std::unordered_map<std::string, Handler> registry = BuildRegistry();
        return registry;
    }

    std::string HandleRequest(const std::string& requestLine) {
        json request;
        try {
            request = json::parse(requestLine);
        } catch (const json::parse_error& e) {
            return MakeResponse("", false, {}, std::string("JSON parse error: ") + e.what()).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        }

        std::string id = request.value("id", "");
        std::string action = request.value("action", "");
        json params = request.value("params", json::object());

        try {
            auto& registry = GetRegistry();
            auto it = registry.find(action);
            if (it == registry.end()) {
                return MakeResponse(id, false, {}, "Unknown action: " + action).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
            }
            return it->second(id, params);
        } catch (const std::exception& e) {
            SKSE::log::error("Error handling action '{}': {}", action, e.what());
            return MakeResponse(id, false, {}, e.what()).dump(-1, ' ', false, nlohmann::json::error_handler_t::replace) + "\n";
        }
    }

}

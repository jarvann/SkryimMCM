#include "UtilityManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <filesystem>
#include <format>

namespace SkyrimMCP::UtilityManager {

    using json = nlohmann::json;

    json ShowNotification(const std::string& message) {
        RE::SendHUDMessage::ShowHUDMessage(message.c_str());
        return {{"shown", true}, {"message", message}};
    }

    // Replaces the "help" console command with a direct form database search.
    // Searches by name (case-insensitive substring match) with optional type filter.
    json SearchForms(const std::string& query, const std::string& formTypeFilter, int maxResults) {
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};

        // Lowercase the query for case-insensitive matching
        std::string lowerQuery = query;
        std::transform(lowerQuery.begin(), lowerQuery.end(), lowerQuery.begin(), ::tolower);

        // Map filter string to FormTypes to search
        struct FormTypeEntry {
            RE::FormType type;
            const char* label;
        };

        std::vector<FormTypeEntry> typesToSearch;

        std::string lowerFilter = formTypeFilter;
        std::transform(lowerFilter.begin(), lowerFilter.end(), lowerFilter.begin(), ::tolower);

        if (lowerFilter.empty() || lowerFilter == "all") {
            typesToSearch = {
                {RE::FormType::Weapon, "weapon"}, {RE::FormType::Armor, "armor"},
                {RE::FormType::AlchemyItem, "potion"}, {RE::FormType::Ingredient, "ingredient"},
                {RE::FormType::Book, "book"}, {RE::FormType::Misc, "misc"},
                {RE::FormType::Ammo, "ammo"}, {RE::FormType::SoulGem, "soulgem"},
                {RE::FormType::KeyMaster, "key"}, {RE::FormType::Scroll, "scroll"},
                {RE::FormType::Spell, "spell"}, {RE::FormType::Perk, "perk"},
                {RE::FormType::Quest, "quest"}, {RE::FormType::NPC, "npc"},
                {RE::FormType::Race, "race"}, {RE::FormType::Enchantment, "enchantment"},
                {RE::FormType::Shout, "shout"}, {RE::FormType::WordOfPower, "wordofpower"},
                {RE::FormType::Location, "location"},
            };
        } else if (lowerFilter == "weapon" || lowerFilter == "weap") {
            typesToSearch = {{RE::FormType::Weapon, "weapon"}};
        } else if (lowerFilter == "armor" || lowerFilter == "armo") {
            typesToSearch = {{RE::FormType::Armor, "armor"}};
        } else if (lowerFilter == "potion" || lowerFilter == "alch") {
            typesToSearch = {{RE::FormType::AlchemyItem, "potion"}};
        } else if (lowerFilter == "book") {
            typesToSearch = {{RE::FormType::Book, "book"}};
        } else if (lowerFilter == "misc") {
            typesToSearch = {{RE::FormType::Misc, "misc"}};
        } else if (lowerFilter == "ingredient" || lowerFilter == "ingr") {
            typesToSearch = {{RE::FormType::Ingredient, "ingredient"}};
        } else if (lowerFilter == "ammo") {
            typesToSearch = {{RE::FormType::Ammo, "ammo"}};
        } else if (lowerFilter == "key") {
            typesToSearch = {{RE::FormType::KeyMaster, "key"}};
        } else if (lowerFilter == "spell" || lowerFilter == "spel") {
            typesToSearch = {{RE::FormType::Spell, "spell"}};
        } else if (lowerFilter == "perk") {
            typesToSearch = {{RE::FormType::Perk, "perk"}};
        } else if (lowerFilter == "quest" || lowerFilter == "qust") {
            typesToSearch = {{RE::FormType::Quest, "quest"}};
        } else if (lowerFilter == "npc" || lowerFilter == "npc_") {
            typesToSearch = {{RE::FormType::NPC, "npc"}};
        } else if (lowerFilter == "enchantment" || lowerFilter == "ench") {
            typesToSearch = {{RE::FormType::Enchantment, "enchantment"}};
        } else if (lowerFilter == "shout" || lowerFilter == "shou") {
            typesToSearch = {{RE::FormType::Shout, "shout"}};
        } else if (lowerFilter == "location" || lowerFilter == "lctn") {
            typesToSearch = {{RE::FormType::Location, "location"}};
        } else if (lowerFilter == "scroll" || lowerFilter == "scrl") {
            typesToSearch = {{RE::FormType::Scroll, "scroll"}};
        } else if (lowerFilter == "soulgem" || lowerFilter == "slgm") {
            typesToSearch = {{RE::FormType::SoulGem, "soulgem"}};
        } else if (lowerFilter == "idle" || lowerFilter == "animation") {
            typesToSearch = {{RE::FormType::Idle, "idle"}};
        } else if (lowerFilter == "race") {
            typesToSearch = {{RE::FormType::Race, "race"}};
        } else if (lowerFilter == "faction") {
            typesToSearch = {{RE::FormType::Faction, "faction"}};
        } else if (lowerFilter == "worldspace") {
            typesToSearch = {{RE::FormType::WorldSpace, "worldspace"}};
        } else if (lowerFilter == "cell") {
            typesToSearch = {{RE::FormType::Cell, "cell"}};
        } else if (lowerFilter == "container" || lowerFilter == "cont") {
            typesToSearch = {{RE::FormType::Container, "container"}};
        } else if (lowerFilter == "activator" || lowerFilter == "acti") {
            typesToSearch = {{RE::FormType::Activator, "activator"}};
        } else if (lowerFilter == "furniture" || lowerFilter == "furn") {
            typesToSearch = {{RE::FormType::Furniture, "furniture"}};
        } else if (lowerFilter == "door") {
            typesToSearch = {{RE::FormType::Door, "door"}};
        } else if (lowerFilter == "flora") {
            typesToSearch = {{RE::FormType::Flora, "flora"}};
        } else if (lowerFilter == "music" || lowerFilter == "musc") {
            typesToSearch = {{RE::FormType::MusicType, "music"}};
        } else if (lowerFilter == "keyword" || lowerFilter == "kywd") {
            typesToSearch = {{RE::FormType::Keyword, "keyword"}};
        } else if (lowerFilter == "magiceffect" || lowerFilter == "mgef") {
            typesToSearch = {{RE::FormType::MagicEffect, "magiceffect"}};
        } else if (lowerFilter == "headpart" || lowerFilter == "hdpt") {
            typesToSearch = {{RE::FormType::HeadPart, "headpart"}};
        } else if (lowerFilter == "eyes") {
            typesToSearch = {{RE::FormType::Eyes, "eyes"}};
        } else if (lowerFilter == "outfit" || lowerFilter == "otft") {
            typesToSearch = {{RE::FormType::Outfit, "outfit"}};
        } else if (lowerFilter == "voicetype" || lowerFilter == "vtyp") {
            typesToSearch = {{RE::FormType::VoiceType, "voicetype"}};
        } else if (lowerFilter == "class" || lowerFilter == "clas") {
            typesToSearch = {{RE::FormType::Class, "class"}};
        } else if (lowerFilter == "combatstyle") {
            typesToSearch = {{RE::FormType::CombatStyle, "combatstyle"}};
        } else if (lowerFilter == "wordofpower" || lowerFilter == "woop") {
            typesToSearch = {{RE::FormType::WordOfPower, "wordofpower"}};
        } else if (lowerFilter == "scene" || lowerFilter == "scen") {
            typesToSearch = {{RE::FormType::Scene, "scene"}};
        } else if (lowerFilter == "relationship" || lowerFilter == "rela") {
            typesToSearch = {{RE::FormType::Relationship, "relationship"}};
        } else if (lowerFilter == "constructible" || lowerFilter == "cobj" || lowerFilter == "recipe") {
            typesToSearch = {{RE::FormType::ConstructibleObject, "recipe"}};
        } else if (lowerFilter == "projectile" || lowerFilter == "proj") {
            typesToSearch = {{RE::FormType::Projectile, "projectile"}};
        } else if (lowerFilter == "light") {
            typesToSearch = {{RE::FormType::Light, "light"}};
        } else if (lowerFilter == "global" || lowerFilter == "glob") {
            typesToSearch = {{RE::FormType::Global, "global"}};
        } else {
            // Default to searching common types
            typesToSearch = {
                {RE::FormType::Weapon, "weapon"}, {RE::FormType::Armor, "armor"},
                {RE::FormType::AlchemyItem, "potion"}, {RE::FormType::Book, "book"},
                {RE::FormType::Misc, "misc"}, {RE::FormType::Quest, "quest"},
                {RE::FormType::NPC, "npc"}, {RE::FormType::Spell, "spell"},
            };
        }

        json results = json::array();
        int count = 0;

        // Debug: log form array size for the requested type
        for (auto& [ft, lb] : typesToSearch) {
            try {
                auto& fa = dataHandler->GetFormArray(ft);
                SKSE::log::info("SearchForms: type '{}' has {} forms (formArray)", lb, fa.size());

                // For idle forms with empty array, count from global map
                if (ft == RE::FormType::Idle && fa.empty()) {
                    const auto& [allForms, lock] = RE::TESForm::GetAllForms();
                    RE::BSReadLockGuard readLock(lock);
                    int idleCount = 0, logged = 0;
                    if (allForms) {
                        for (auto& [id, f] : *allForms) {
                            if (f && f->GetFormType() == RE::FormType::Idle) {
                                idleCount++;
                                if (logged < 3) {
                                    auto* idle = f->As<RE::TESIdleForm>();
                                    if (idle) {
                                        SKSE::log::info("  Idle {:08X}: name='{}' editorId='{}' animFile='{}' animEvent='{}'",
                                            idle->GetFormID(),
                                            idle->GetName() ? idle->GetName() : "(null)",
                                            idle->GetFormEditorID() ? idle->GetFormEditorID() : "(null)",
                                            idle->animFileName.c_str() ? idle->animFileName.c_str() : "(null)",
                                            idle->animEventName.c_str() ? idle->animEventName.c_str() : "(null)");
                                        logged++;
                                    }
                                }
                            }
                        }
                    }
                    SKSE::log::info("SearchForms: type '{}' has {} forms (global map fallback)", lb, idleCount);
                }
            } catch (...) {}
        }

        // Helper lambda: check if a form matches the query and add to results
        auto tryMatchForm = [&](RE::TESForm* form, RE::FormType formType, const char* label) -> bool {
            if (count >= maxResults) return false;
            if (!form) return false;

            try {
                const char* name = form->GetName();
                const char* editorId = form->GetFormEditorID();

                bool matched = false;
                if (name && name[0]) {
                    std::string lowerName(name);
                    std::transform(lowerName.begin(), lowerName.end(), lowerName.begin(), ::tolower);
                    if (lowerName.find(lowerQuery) != std::string::npos) matched = true;
                }
                if (!matched && editorId && editorId[0]) {
                    std::string lowerEditorId(editorId);
                    std::transform(lowerEditorId.begin(), lowerEditorId.end(), lowerEditorId.begin(), ::tolower);
                    if (lowerEditorId.find(lowerQuery) != std::string::npos) matched = true;
                }

                // For idle forms, also search animation file/event names
                if (!matched && formType == RE::FormType::Idle) {
                    auto* idle = form->As<RE::TESIdleForm>();
                    if (idle) {
                        std::string animFile = idle->animFileName.c_str() ? idle->animFileName.c_str() : "";
                        std::string animEvent = idle->animEventName.c_str() ? idle->animEventName.c_str() : "";
                        std::string lowerFile = animFile;
                        std::string lowerEvent = animEvent;
                        std::transform(lowerFile.begin(), lowerFile.end(), lowerFile.begin(), ::tolower);
                        std::transform(lowerEvent.begin(), lowerEvent.end(), lowerEvent.begin(), ::tolower);
                        if ((!lowerFile.empty() && lowerFile.find(lowerQuery) != std::string::npos) ||
                            (!lowerEvent.empty() && lowerEvent.find(lowerQuery) != std::string::npos)) {
                            matched = true;
                        }
                    }
                }

                if (matched) {
                    json r;
                    r["formId"] = std::format("{:08X}", form->GetFormID());
                    r["name"] = name ? name : "";
                    r["editorId"] = editorId ? editorId : "";
                    r["type"] = label;

                    if (formType == RE::FormType::Idle) {
                        auto* idle = form->As<RE::TESIdleForm>();
                        if (idle) {
                            r["animFile"] = idle->animFileName.c_str() ? idle->animFileName.c_str() : "";
                            r["animEvent"] = idle->animEventName.c_str() ? idle->animEventName.c_str() : "";
                        }
                    }

                    results.push_back(r);
                    count++;
                    return true;
                }
            } catch (...) {}
            return false;
        };

        for (auto& [formType, label] : typesToSearch) {
            if (count >= maxResults) break;

            try {
                auto& forms = dataHandler->GetFormArray(formType);

                // Some form types (notably Idle) have empty form arrays in TESDataHandler.
                // Fall back to the global form map filtered by FormType.
                if (forms.empty() && formType == RE::FormType::Idle) {
                    SKSE::log::info("SearchForms: formArray empty for '{}', falling back to global form map", label);
                    const auto& [allForms, lock] = RE::TESForm::GetAllForms();
                    RE::BSReadLockGuard readLock(lock);
                    if (allForms) {
                        for (auto& [id, form] : *allForms) {
                            if (count >= maxResults) break;
                            if (form && form->GetFormType() == formType) {
                                tryMatchForm(form, formType, label);
                            }
                        }
                    }
                    continue;
                }

                for (auto* form : forms) {
                    if (count >= maxResults) break;
                    tryMatchForm(form, formType, label);
                }
            } catch (...) {
                continue;
            }
        }

        return {{"results", results}, {"count", count}, {"query", query}};
    }

    json GetLoadedSKSEPlugins() {
        json plugins = json::array();

        // Scan SKSE plugins directory
        try {
            auto skyrimPath = std::filesystem::current_path();
            auto pluginsPath = skyrimPath / "Data" / "SKSE" / "Plugins";

            if (std::filesystem::exists(pluginsPath)) {
                for (auto& entry : std::filesystem::directory_iterator(pluginsPath)) {
                    if (entry.is_regular_file() && entry.path().extension() == ".dll") {
                        auto name = entry.path().filename().string();

                        json p;
                        p["name"] = name;
                        p["path"] = entry.path().string();
                        p["sizeBytes"] = entry.file_size();

                        // Flag known plugins with integration potential
                        std::string lowerName = name;
                        std::transform(lowerName.begin(), lowerName.end(), lowerName.begin(), ::tolower);

                        if (lowerName.find("consoleutilsse") != std::string::npos ||
                            lowerName.find("consoleutil") != std::string::npos) {
                            p["integration"] = "console_commands";
                            p["note"] = "Can execute console commands reliably — potential fix for CompileAndRun issues";
                        } else if (lowerName.find("hdtsmp") != std::string::npos) {
                            p["integration"] = "physics";
                            p["note"] = "HDT-SMP physics — potential physics state queries";
                        } else if (lowerName.find("papyrusutil") != std::string::npos) {
                            p["integration"] = "papyrus_bridge";
                            p["note"] = "PapyrusUtil — file I/O and JSON storage from scripts";
                        } else if (lowerName.find("jcontainers") != std::string::npos) {
                            p["integration"] = "data_storage";
                            p["note"] = "JContainers — advanced data structures for scripts";
                        } else if (lowerName.find("po3_tweaks") != std::string::npos ||
                                   lowerName.find("powerofthree") != std::string::npos) {
                            p["integration"] = "engine_tweaks";
                            p["note"] = "powerofthree's Tweaks — extended engine functionality";
                        } else if (lowerName.find("skee") != std::string::npos ||
                                   lowerName.find("racemenu") != std::string::npos) {
                            p["integration"] = "appearance";
                            p["note"] = "RaceMenu/SKEE — advanced character appearance customization API";
                        } else if (lowerName.find("enb") != std::string::npos) {
                            p["integration"] = "graphics";
                            p["note"] = "ENB — graphics post-processing";
                        }

                        plugins.push_back(p);
                    }
                }
            }
        } catch (const std::exception& e) {
            return {{"error", std::string("Failed to scan plugins: ") + e.what()}};
        }

        return {{"plugins", plugins}, {"count", plugins.size()}};
    }

    json SaveGame(const std::string& saveName) {
        auto* saveLoadManager = RE::BGSSaveLoadManager::GetSingleton();
        if (!saveLoadManager) {
            return {{"error", "Save manager not available"}};
        }

        saveLoadManager->Save(saveName.c_str());
        RE::SendHUDMessage::ShowHUDMessage("Game Saved");
        return {{"saved", true}, {"saveName", saveName}};
    }

    json GetLoadOrder() {
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};

        json mods = json::array();

        // Regular plugins (xx prefix)
        auto loadedModCount = dataHandler->GetLoadedModCount();
        auto** loadedMods = dataHandler->GetLoadedMods();
        for (std::uint8_t i = 0; i < loadedModCount; i++) {
            auto* mod = loadedMods[i];
            if (!mod) continue;

            json m;
            m["name"] = mod->fileName;
            m["index"] = i;
            m["prefix"] = std::format("{:02X}", i);
            m["type"] = "esp";
            mods.push_back(m);
        }

        // Light plugins (FExxx prefix)
        auto lightModCount = dataHandler->GetLoadedLightModCount();
        auto** lightMods = dataHandler->GetLoadedLightMods();
        for (std::uint16_t i = 0; i < lightModCount; i++) {
            auto* mod = lightMods[i];
            if (!mod) continue;

            json m;
            m["name"] = mod->fileName;
            m["index"] = i;
            m["prefix"] = std::format("FE{:03X}", i);
            m["type"] = "esl";
            mods.push_back(m);
        }

        return {{"mods", mods}, {"regularCount", loadedModCount}, {"lightCount", lightModCount}};
    }

    json GetModFormIdPrefix(const std::string& modName) {
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};

        // Try regular mod first
        auto modIndex = dataHandler->GetLoadedModIndex(modName);
        if (modIndex.has_value()) {
            return {
                {"mod", modName},
                {"type", "esp"},
                {"index", modIndex.value()},
                {"prefix", std::format("{:02X}", modIndex.value())},
                {"formIdFormat", std::format("{:02X}xxxxxx", modIndex.value())}
            };
        }

        // Try light mod
        auto lightIndex = dataHandler->GetLoadedLightModIndex(modName);
        if (lightIndex.has_value()) {
            return {
                {"mod", modName},
                {"type", "esl"},
                {"index", lightIndex.value()},
                {"prefix", std::format("FE{:03X}", lightIndex.value())},
                {"formIdFormat", std::format("FE{:03X}xxx", lightIndex.value())}
            };
        }

        return {{"error", "Mod not found in load order: " + modName}};
    }

    json LoadSave(const std::string& saveName) {
        auto* mgr = RE::BGSSaveLoadManager::GetSingleton();
        if (!mgr) return {{"error", "Save manager not available"}};

        mgr->Load(saveName.c_str());
        return {{"loading", true}, {"saveName", saveName}};
    }

    json LoadMostRecentSave() {
        auto* mgr = RE::BGSSaveLoadManager::GetSingleton();
        if (!mgr) return {{"error", "Save manager not available"}};

        bool success = mgr->LoadMostRecentSaveGame();
        return {{"loading", success}};
    }

}

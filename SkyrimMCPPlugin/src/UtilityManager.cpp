#include "UtilityManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <filesystem>
#include <format>
#include <string_view>
#include <vector>

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

    namespace {
        std::string ToLower(std::string_view text) {
            std::string out(text);
            std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            return out;
        }

        // BUG-013: say why a plugin has no prefix instead of a generic "not found", and
        // suggest active plugins with a similar name.
        std::string DescribeMissingMod(RE::TESDataHandler* dataHandler, const std::string& modName) {
            // Whether inactive plugins appear in TESDataHandler::files is not confirmed on every
            // runtime, so only claim "inactive" when the game says so (compile index 0xFF).
            const auto* known = dataHandler->LookupModByName(modName);
            if (known && known->GetCompileIndex() == 0xFF) {
                return std::format("{} is installed but not active, so it has no load-order prefix. "
                                   "Enable it in your mod manager (or plugins.txt) and restart Skyrim.",
                                   known->GetFilename());
            }

            // Close matches among the active plugins: same name with another extension, or
            // a name that contains what was asked for.
            const auto wanted = ToLower(modName);
            const auto dot = wanted.rfind('.');
            const auto stem = dot == std::string::npos ? wanted : wanted.substr(0, dot);
            // Same base name first (Skyrim -> Skyrim.esm), then the rest in load order.
            std::vector<std::string> exact, partial;
            for (auto* file : dataHandler->files) {
                if (!file || file->GetCompileIndex() == 0xFF) continue;
                const auto name = ToLower(file->GetFilename());
                if (stem.empty() || name.find(stem) == std::string::npos) continue;
                const auto nameDot = name.rfind('.');
                const auto nameStem = nameDot == std::string::npos ? name : name.substr(0, nameDot);
                (nameStem == stem ? exact : partial).emplace_back(file->GetFilename());
            }
            const std::size_t total = exact.size() + partial.size();
            std::vector<std::string> matches = exact;
            for (const auto& m : partial) {
                if (matches.size() >= 10) break;
                matches.push_back(m);
            }

            // A name with no extension is a search, not a file name: don't claim it isn't installed.
            const bool looksLikeFile = dot != std::string::npos;
            std::string message = looksLikeFile
                ? std::format("{} is not an active plugin: it is not installed, or your mod manager has it disabled.", modName)
                : std::format("No active plugin is named exactly \"{}\" (include the extension, e.g. .esp, .esm or .esl).", modName);
            if (!matches.empty()) {
                message += " Active plugins with a similar name: ";
                for (std::size_t i = 0; i < matches.size(); ++i) message += (i ? ", " : "") + matches[i];
                if (total > matches.size()) message += std::format(" (and {} more)", total - matches.size());
                message += ".";
            }
            return message;
        }
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

        return {{"error", DescribeMissingMod(dataHandler, modName)}};
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

    namespace {
        std::string CategoryName(RE::BGSSoundCategory* c) {
            const char* name = c->GetFullName();
            if (name && name[0]) return name[0] == '$' && name[1] ? name + 1 : name;  // "$Music" is a localisation key
            const char* edid = c->GetFormEditorID();
            return (edid && edid[0]) ? edid : std::format("{:08X}", c->GetFormID());
        }

        std::string LowerCopy(std::string s) {
            std::transform(s.begin(), s.end(), s.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
            return s;
        }

        // Category names are localisation keys such as "$Music" (BUG-027): compare without the "$"
        // and without case, so "Music", "music" and "$Music" all match.
        std::string CategoryKey(std::string s) {
            if (!s.empty() && s.front() == '$') s.erase(0, 1);
            return LowerCopy(s);
        }
    }

    // FEAT-131: the Audio menu's sliders are sound categories (Master, Effects, Music, Voice...);
    // setting a category's volume is what the menu does. The old tool ran "setgs
    // fAudioMasterVolume", an INI preference the console can't change, so nothing happened.
    namespace {
        std::string FormHex(const RE::TESForm* f) { return std::format("{:08X}", f->GetFormID()); }

        // BUG-059: the game's own value is volumeMult (what the Audio menu slider sets); GetCategoryVolume() is the
        // effective volume, which also carries the static multiplier and the parent categories (Music read 0.7 of what was
        // set, Menus 0.3: Effects' value). Restores need the own value; listing only the effective one made "previous" wrong.
        json DescribeCategory(RE::BGSSoundCategory* c) {
            json j = {{"name", CategoryName(c)}, {"formId", FormHex(c)}, {"own", c->volumeMult},
                      {"effective", c->GetCategoryVolume()}, {"staticMultiplier", c->staticMult / 65535.0f}};
            if (c->parentCategory) j["parent"] = CategoryName(c->parentCategory) + " (" + FormHex(c->parentCategory) + ")";
            return j;
        }

        int Depth(const RE::BGSSoundCategory* c) {
            int d = 0;
            for (auto* p = c->parentCategory; p && d < 16; p = p->parentCategory) ++d;
            return d;
        }
    }

    // FEAT-131: the Audio menu's sliders are sound categories (Master, Effects, Music, Voice...);
    // setting a category's volume is what the menu does. The old tool ran "setgs
    // fAudioMasterVolume", an INI preference the console can't change, so nothing happened.
    json SetVolume(const std::string& category, float volume) {
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Game data not ready"}};

        std::vector<RE::BGSSoundCategory*> menu;
        for (auto* c : dataHandler->GetFormArray<RE::BGSSoundCategory>()) {
            if (c && c->IsMenuCategory()) menu.push_back(c);
        }
        // Parents first, so a restore in list order sets a parent before its children.
        std::stable_sort(menu.begin(), menu.end(), [](auto* a, auto* b) { return Depth(a) < Depth(b); });
        json listed = json::array();
        for (auto* c : menu) listed.push_back(DescribeCategory(c));
        auto* prefs = RE::INIPrefSettingCollection::GetSingleton();
        auto* masterSetting = prefs ? prefs->GetSetting("fAudioMasterVolume:AudioMenu") : nullptr;
        if (category == "list") {
            return {{"categories", listed},
                    {"master", masterSetting ? json(masterSetting->data.f) : json(nullptr)},
                    {"note", "own: the category's own value (what SetVolume sets and what to restore); effective: what plays, "
                             "after the static multiplier and the parent categories. Parents are listed first. Master is "
                             "fAudioMasterVolume, not a category."}};
        }

        const auto wanted = CategoryKey(category.empty() ? "master" : category);
        volume = std::clamp(volume, 0.0f, 1.0f);

        // The Master slider isn't a sound category (found in game: the old fallback picked
        // "Magic"). It is the fAudioMasterVolume preference, which the audio manager keeps as
        // its own masterVolume.
        if (wanted == "master") {
            auto* audio = RE::BSAudioManager::GetSingleton();
            if (!masterSetting && !audio) return {{"error", "Neither the master volume setting nor the audio manager is available"}};
            json result = {{"category", "Master"}};
            if (masterSetting) {
                result["previous"] = masterSetting->data.f;
                masterSetting->data.f = volume;
            }
            if (audio) {
                if (!result.contains("previous")) result["previous"] = audio->masterVolume;
                audio->masterVolume = volume;
            }
            result["volume"] = volume;
            result["note"] = "Set the Audio menu's master value. Ask the player whether they hear the change: if not, it applies "
                             "when the Audio menu is next opened and closed. Saved to SkyrimPrefs.ini when the game saves its settings.";
            return result;
        }

        auto apply = [&](RE::BGSSoundCategory* c) {
            const float before = c->volumeMult;
            c->SetCategoryVolume(volume);
            json r = DescribeCategory(c);
            r["previous"] = before;
            r["volume"] = c->volumeMult;
            if (std::abs(c->volumeMult - volume) > 0.01f) {
                r["warning"] = std::format("The category's own value reads {:.2f} after setting {:.2f}", c->volumeMult, volume);
            }
            return r;
        };

        // BUG-059: "all" sets every Audio menu category (not Master) in one call.
        if (wanted == "all") {
            json changed = json::array();
            for (auto* c : menu) changed.push_back(apply(c));
            return {{"category", "all"}, {"changed", changed}, {"note", "previous is each category's own value: restore with those, parents first."}};
        }

        // BUG-059: a FormID picks one category when two share a name (Weapons and Footsteps exist twice with some mods).
        std::vector<RE::BGSSoundCategory*> found;
        for (auto* c : menu) {
            const char* edid = c->GetFormEditorID();
            if (CategoryKey(CategoryName(c)) == wanted || (edid && CategoryKey(edid) == wanted) || LowerCopy(FormHex(c)) == wanted ||
                LowerCopy("0x" + FormHex(c)) == wanted) {
                found.push_back(c);
            }
        }
        if (found.empty()) return {{"error", "No Audio menu category named '" + category + "'"}, {"categories", listed}};

        json result = apply(found.front());
        result["category"] = CategoryName(found.front());
        if (found.size() > 1) {
            json others = json::array();
            for (size_t i = 1; i < found.size(); ++i) others.push_back(FormHex(found[i]));
            result["sameName"] = others;
            result["sameNameNote"] = "Other categories have this name; this set only the first. Pass a FormID to set another.";
        }
        if (CategoryKey(result["category"].get<std::string>()).find("music") != std::string::npos) {
            result["note"] = "The track already playing may keep its volume (heard in game: music went on at 0); the next track "
                             "uses the new value, or StopMusic to change track now.";
        } else {
            result["note"] = "Applies at once, like the Audio menu slider. The Audio menu may show the old position until reopened.";
        }
        return result;
    }

    // FEAT-133: queue a screenshot through the game's own Print Screen handler. The game only
    // writes it when bAllowScreenShot (Skyrim.ini [Display]) is on, so that is switched on for
    // this session if needed. The caller checks the file actually appeared.
    json TakeScreenshot() {
        auto* controls = RE::MenuControls::GetSingleton();
        auto* handler = controls ? controls->screenshotHandler : nullptr;
        if (!handler) return {{"error", "The game's screenshot handler isn't available"}};

        json result;
        if (auto* ini = RE::INISettingCollection::GetSingleton()) {
            if (auto* allow = ini->GetSetting("bAllowScreenShot:Display")) {
                result["allowScreenShotWas"] = allow->data.b;
                if (!allow->data.b) {
                    allow->data.b = true;
                    result["allowScreenShotChanged"] = "Turned bAllowScreenShot on for this session (Skyrim.ini is not changed)";
                }
            } else {
                result["allowScreenShotWas"] = nullptr;
            }
        }
        // Where the game writes it: sScreenShotBaseName is a path prefix ("ScreenShot" in the game
        // folder by default; players often point it elsewhere, e.g. Pictures).
        if (auto* ini = RE::INISettingCollection::GetSingleton()) {
            if (auto* base = ini->GetSetting("sScreenShotBaseName:Display")) {
                const char* text = base->GetString();
                result["baseName"] = (text && text[0]) ? text : "ScreenShot";
            }
        }
        handler->screenshotQueued = true;
        result["queued"] = true;
        return result;
    }

}

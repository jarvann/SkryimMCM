#include "BlueprintManager.h"
#include "Helpers.h"
#include "InventoryManager.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <format>
#include <set>
#include <string>
#include <unordered_map>

namespace SkyrimMCP::BlueprintManager {

    namespace {
        constexpr RE::FormID kGold = 0x0000000F;

        bool IsFormIdKey(const std::string& key) {
            if (key == "formId" || key == "baseId") return true;
            return key.size() > 6 && key.ends_with("FormId");
        }

        bool ParseHexId(const json& value, RE::FormID& id) {
            if (!value.is_string()) return false;
            auto text = value.get<std::string>();
            if (text.starts_with("0x") || text.starts_with("0X")) text = text.substr(2);
            if (text.empty() || text.size() > 8 || !std::all_of(text.begin(), text.end(), [](unsigned char c) { return std::isxdigit(c); })) return false;
            id = static_cast<RE::FormID>(std::stoul(text, nullptr, 16));
            return true;
        }

        json PortableOf(RE::FormID id) {
            if ((id >> 24) == 0xFF) return {{"created", true}};
            auto* form = RE::TESForm::LookupByID(id);
            auto* file = form ? form->GetFile(0) : nullptr;
            if (!file) return nullptr;
            const RE::FormID local = file->IsLight() ? (id & 0xFFF) : (id & 0xFFFFFF);
            return {{"plugin", file->GetFilename()}, {"localId", std::format("{:06X}", local)}};
        }

        // Resolves one blueprint entry back to a form in this game: by plugin + local ID when the
        // blueprint has one, otherwise by the raw form ID. how says which, for the report.
        RE::TESForm* Resolve(const json& entry, std::string& how) {
            auto* dataHandler = RE::TESDataHandler::GetSingleton();
            if (auto src = entry.find("formIdSource"); src != entry.end() && src->is_object()) {
                if (src->value("created", false)) { how = "created in game (can't be recreated)"; return nullptr; }
                const auto plugin = src->value("plugin", "");
                const auto local = src->value("localId", "");
                if (!plugin.empty() && !local.empty() && dataHandler) {
                    const auto* file = dataHandler->LookupModByName(plugin);
                    if (!file) { how = "plugin not loaded: " + plugin; return nullptr; }
                    try {
                        // LookupForm adds the ID to the plugin's prefix unmasked: keep the plugin's own bits.
                        const auto id = static_cast<RE::FormID>(std::stoul(local, nullptr, 16)) & (file->IsLight() ? 0xFFFu : 0xFFFFFFu);
                        if (auto* form = dataHandler->LookupForm(id, plugin)) {
                            how = "plugin";
                            return form;
                        }
                    } catch (...) {}
                    how = "not found in " + plugin;
                    return nullptr;
                }
            }
            RE::FormID id = 0;
            if (!ParseHexId(entry.value("formId", json()), id)) { how = "no formId"; return nullptr; }
            if ((id >> 24) == 0xFF) { how = "created in game (can't be recreated)"; return nullptr; }
            auto* form = RE::TESForm::LookupByID(id);
            if (!form) { how = "form not found"; return nullptr; }
            // Without a plugin name the raw ID may point at a different form after a load-order
            // change: only trust it if the names agree.
            const auto wantedName = entry.value("name", "");
            const char* name = form->GetName();
            if (!wantedName.empty() && (!name || wantedName != name)) {
                how = std::format("form ID now points at a different item ({}); export the blueprint again with 1.3 for plugin names", name && name[0] ? name : "unnamed");
                return nullptr;
            }
            how = "formId";
            return form;
        }

        json Label(const json& entry) {
            return {{"name", entry.value("name", "")}, {"formId", entry.value("formId", "")}};
        }
    }

    void AddPortableIds(json& node) {
        if (node.is_array()) {
            for (auto& child : node) AddPortableIds(child);
            return;
        }
        if (!node.is_object()) return;
        json additions = json::object();
        for (auto it = node.begin(); it != node.end(); ++it) {
            RE::FormID id = 0;
            if (IsFormIdKey(it.key()) && ParseHexId(it.value(), id) && id != 0) {
                auto portable = PortableOf(id);
                if (!portable.is_null()) additions[it.key() + "Source"] = std::move(portable);
            } else if (it.value().is_structured()) {
                AddPortableIds(it.value());
            }
        }
        for (auto& [key, value] : additions.items()) node[key] = value;
    }

    json ApplyBlueprintItems(const json& params) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        std::set<std::string> sections;
        for (const auto& s : params.value("sections", json::array({"inventory", "equipment", "gold"}))) {
            if (s.is_string()) sections.insert(s.get<std::string>());
        }
        const bool dryRun = params.value("dryRun", false);

        // What the player has now, by form, and what she is wearing (BUG-061: a dry run on her own unchanged blueprint
        // listed all 17 equipped items as to-equip).
        std::unordered_map<RE::FormID, int> have;
        std::set<RE::FormID> worn;
        for (auto& [object, data] : player->GetInventory()) {
            if (!object) continue;
            have[object->GetFormID()] += data.first;
            if (data.second && (data.second->IsWorn() || data.second->IsWorn(true))) worn.insert(object->GetFormID());
        }
        for (bool left : {false, true}) {
            if (auto* f = player->GetEquippedObject(left)) worn.insert(f->GetFormID());
        }
        if (auto* power = player->GetActorRuntimeData().selectedPower) worn.insert(power->GetFormID());

        json report;
        report["dryRun"] = dryRun;

        if (sections.contains("inventory")) {
            json added = json::array(), alreadyHave = json::array(), unresolved = json::array(), notRestored = json::array();
            int stacks = 0;
            for (const auto& entry : params.value("inventory", json::array())) {
                if (!entry.is_object()) continue;
                const int wanted = entry.value("count", 0);
                if (wanted <= 0) continue;
                std::string how;
                auto* form = Resolve(entry, how);
                auto* object = form ? form->As<RE::TESBoundObject>() : nullptr;
                if (!object) { auto l = Label(entry); l["reason"] = form ? "not an item" : how; unresolved.push_back(l); continue; }
                if (object->GetFormID() == kGold) continue;  // the gold section handles it
                const int missing = wanted - have[object->GetFormID()];
                if (missing <= 0) { alreadyHave.push_back(entry.value("name", "")); continue; }

                // Player-made enchantments and tempering live on the item instance, not the form.
                if (entry.contains("enchantment")) {
                    auto* enchantable = object->As<RE::TESEnchantableForm>();
                    if (!enchantable || !enchantable->formEnchanting) notRestored.push_back(Label(entry));
                }
                json a = Label(entry);
                a["count"] = missing;
                a["blueprintCount"] = wanted;
                a["has"] = have[object->GetFormID()];
                a["resolvedBy"] = how;
                RE::FormID oldId = 0;
                if (ParseHexId(entry.value("formId", json()), oldId) && form->GetFormID() != oldId) a["formIdNow"] = std::format("{:08X}", form->GetFormID());
                added.push_back(a);
                ++stacks;
                if (!dryRun) {
                    player->AddObjectToContainer(object, nullptr, missing, nullptr);
                    have[object->GetFormID()] += missing;
                }
            }
            report["inventory"] = {
                {"added", added}, {"stacks", stacks}, {"alreadyHad", alreadyHave.size()},
                {"unresolved", unresolved},
            };
            if (!notRestored.empty()) {
                report["inventory"]["enchantmentsNotRestored"] = notRestored;
                report["inventory"]["enchantmentNote"] = "These had enchantments made by the player; the plain item was added.";
            }
        }

        if (sections.contains("gold") && params.contains("gold")) {
            const int target = params.value("gold", 0);
            const int current = have[kGold];
            int change = 0;
            if (target > current) change = target - current;
            else if (target < current && params.value("exactGold", false)) change = target - current;
            if (!dryRun && change != 0) {
                if (auto* gold = RE::TESForm::LookupByID<RE::TESBoundObject>(kGold)) {
                    if (change > 0) player->AddObjectToContainer(gold, nullptr, change, nullptr);
                    else player->RemoveItem(gold, -change, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
                }
            }
            report["gold"] = {{"before", current}, {"target", target}, {"change", change}};
        }

        // FEAT-121: skills, level, perks, spells and shouts. Only ever raises or adds: nothing is lowered or removed unless
        // lowerSkills is set (skills only). Spells come before equipment so a spell in her hands can be equipped.
        if (sections.contains("skills")) {
            static const std::pair<const char*, RE::ActorValue> kSkills[] = {
                {"oneHanded", RE::ActorValue::kOneHanded}, {"twoHanded", RE::ActorValue::kTwoHanded}, {"archery", RE::ActorValue::kArchery},
                {"block", RE::ActorValue::kBlock}, {"smithing", RE::ActorValue::kSmithing}, {"heavyArmor", RE::ActorValue::kHeavyArmor},
                {"lightArmor", RE::ActorValue::kLightArmor}, {"pickpocket", RE::ActorValue::kPickpocket}, {"lockpicking", RE::ActorValue::kLockpicking},
                {"sneak", RE::ActorValue::kSneak}, {"alchemy", RE::ActorValue::kAlchemy}, {"speech", RE::ActorValue::kSpeech},
                {"alteration", RE::ActorValue::kAlteration}, {"conjuration", RE::ActorValue::kConjuration}, {"destruction", RE::ActorValue::kDestruction},
                {"illusion", RE::ActorValue::kIllusion}, {"restoration", RE::ActorValue::kRestoration}, {"enchanting", RE::ActorValue::kEnchanting},
            };
            const bool lower = params.value("lowerSkills", false);
            auto* avo = player->AsActorValueOwner();
            json changed = json::array(), same = json::array();
            for (const auto& entry : params.value("skills", json::array())) {
                if (!entry.is_object()) continue;
                const auto name = entry.value("name", "");
                const float target = entry.value("baseLevel", entry.value("level", 0.0f));
                const auto it = std::find_if(std::begin(kSkills), std::end(kSkills), [&](const auto& k) { return name == k.first; });
                if (it == std::end(kSkills) || !avo || target <= 0) continue;
                const float now = avo->GetBaseActorValue(it->second);
                if (std::abs(target - now) < 0.5f || (target < now && !lower)) { same.push_back(name); continue; }
                if (!dryRun) avo->SetBaseActorValue(it->second, target);
                changed.push_back({{"skill", name}, {"from", now}, {"to", target}});
            }
            report["skills"] = {{"changed", changed}, {"unchanged", same.size()},
                                {"note", lower ? "Skills set to the blueprint's base levels." : "Only skills below the blueprint's were raised (lowerSkills=true also lowers)."}};
        }

        if (sections.contains("level") && params.contains("level")) {
            const int target = params.value("level", 0);
            auto* base = player->GetActorBase();
            const int now = player->GetLevel();
            if (base && target > now) {
                if (!dryRun) base->actorData.level = static_cast<std::uint16_t>(target);
                report["level"] = {{"from", now}, {"to", target}, {"note", "Perk points and the level-up health, magicka and stamina choices aren't given: set those with the player."}};
            } else {
                report["level"] = {{"from", now}, {"to", now}, {"note", "Already at or above the blueprint's level; levels are never lowered."}};
            }
        }

        auto addForms = [&](const char* key, const char* section, auto has, auto add) {
            json added = json::array(), already = json::array(), unresolved = json::array();
            for (const auto& entry : params.value(key, json::array())) {
                if (!entry.is_object()) continue;
                std::string how;
                auto* form = Resolve(entry, how);
                if (!form) { auto l = Label(entry); l["reason"] = how; unresolved.push_back(l); continue; }
                if (has(form)) { already.push_back(entry.value("name", "")); continue; }
                if (!dryRun && !add(form)) { auto l = Label(entry); l["reason"] = "the game refused it"; unresolved.push_back(l); continue; }
                added.push_back(entry.value("name", ""));
            }
            report[section] = {{"added", added}, {"alreadyKnown", already.size()}, {"unresolved", unresolved}};
        };

        if (sections.contains("perks")) {
            addForms("perks", "perks",
                [&](RE::TESForm* f) { auto* perk = f->As<RE::BGSPerk>(); return !perk || player->HasPerk(perk); },
                [&](RE::TESForm* f) { player->AddPerk(f->As<RE::BGSPerk>()); return true; });
        }
        if (sections.contains("spells")) {
            addForms("spells", "spells",
                [&](RE::TESForm* f) { auto* spell = f->As<RE::SpellItem>(); return !spell || player->HasSpell(spell); },
                [&](RE::TESForm* f) { return player->AddSpell(f->As<RE::SpellItem>()); });
        }
        if (sections.contains("shouts")) {
            // The shout itself; its words are taught and unlocked by the server through Papyrus (Game.TeachWord/UnlockWord).
            addForms("shouts", "shouts",
                [&](RE::TESForm* f) {
                    auto* shout = f->As<RE::TESShout>();
                    if (!shout) return true;
                    auto* list = player->GetActorBase() ? player->GetActorBase()->GetSpellList() : nullptr;
                    if (list) for (std::uint32_t i = 0; i < list->numShouts; ++i) if (list->shouts[i] == shout) return true;
                    return false;
                },
                [&](RE::TESForm* f) { return player->AddShout(f->As<RE::TESShout>()); });
        }

        if (sections.contains("equipment")) {
            json toEquip = json::array(), unresolved = json::array(), planned = json::array(), alreadyEquipped = json::array();
            json spellsSkipped = json::array();
            for (const auto& entry : params.value("equipped", json::array())) {
                if (!entry.is_object() || entry.value("empty", false)) continue;
                std::string how;
                auto* form = Resolve(entry, how);
                if (!form) { auto l = Label(entry); l["reason"] = how; unresolved.push_back(l); continue; }
                if (worn.contains(form->GetFormID())) { alreadyEquipped.push_back(entry.value("name", "")); continue; }
                // BUG-082: spells aren't restored, so an unlearned spell ended up in her hands (and nothing can unequip it).
                if (auto* spell = form->As<RE::SpellItem>()) {
                    if (!player->HasSpell(spell)) { spellsSkipped.push_back(entry.value("name", "")); continue; }
                }
                // BUG-082: with only the equipment section, 15 of 17 items failed "not in the inventory". Equipping needs the
                // item, so add one when she has none.
                bool adding = false;
                if (auto* object = form->As<RE::TESBoundObject>(); object && !form->As<RE::SpellItem>() && have[form->GetFormID()] <= 0) {
                    adding = true;
                    if (!dryRun) {
                        player->AddObjectToContainer(object, nullptr, 1, nullptr);
                        have[form->GetFormID()] += 1;
                    }
                }
                json item = {{"formId", std::format("{:08X}", form->GetFormID())}};
                const auto slot = entry.value("slot", "");
                if (slot == "rightHand") item["hand"] = "right";
                else if (slot == "leftHand") item["hand"] = "left";
                auto p = Label(entry);
                if (item.contains("hand")) p["hand"] = item["hand"];
                if (adding) p["added"] = true;
                planned.push_back(p);
                toEquip.push_back(item);
            }
            json equipReport = {{"items", planned}, {"alreadyEquipped", alreadyEquipped.size()}, {"unresolved", unresolved}};
            if (!spellsSkipped.empty()) {
                equipReport["spellsNotKnown"] = spellsSkipped;
                equipReport["spellNote"] = "Spells aren't restored by the blueprint; these weren't equipped because she doesn't know them.";
            }
            if (!dryRun && !toEquip.empty()) {
                auto r = InventoryManager::EquipItems(toEquip, "", false);
                equipReport["succeeded"] = r.value("succeeded", 0);
                equipReport["failed"] = r.value("failed", 0);
                json failures = json::array();
                for (const auto& x : r.value("results", json::array())) if (!x.value("ok", false)) failures.push_back(x);
                if (!failures.empty()) equipReport["failures"] = failures;
            }
            report["equipment"] = equipReport;
        }

        if (!sections.empty()) {
            report["note"] = dryRun ? std::string("Nothing was changed. Call again with dryRun=false to apply.") +
                                          (sections.contains("equipment") ? " Items marked added would be added to equip them." : "")
                                    : "Only missing items were added (and one of each item to be equipped); nothing was removed or unequipped.";
        }
        return report;
    }

}

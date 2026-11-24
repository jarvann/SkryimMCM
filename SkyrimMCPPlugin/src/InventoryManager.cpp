#include "InventoryManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <format>
#include <unordered_set>

namespace SkyrimMCP::InventoryManager {

    using json = nlohmann::json;

    json AddItem(const std::string& formIdHex, int count) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(formIdHex));
            if (!form) return {{"error", "Form not found: " + formIdHex}};

            auto* boundObj = form->As<RE::TESBoundObject>();
            if (!boundObj) return {{"error", "Not a valid item: " + formIdHex}};

            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};

            player->AddObjectToContainer(boundObj, nullptr, count, nullptr);
            return {{"added", true}, {"formId", formIdHex}, {"count", count}, {"name", form->GetName()}};
        } catch (...) {
            return {{"error", "Invalid FormID: " + formIdHex}};
        }
    }

    json RemoveItem(const std::string& formIdHex, int count) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(formIdHex));
            if (!form) return {{"error", "Form not found: " + formIdHex}};

            auto* boundObj = form->As<RE::TESBoundObject>();
            if (!boundObj) return {{"error", "Not a valid item: " + formIdHex}};

            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};

            player->RemoveItem(boundObj, count, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
            return {{"removed", true}, {"formId", formIdHex}, {"count", count}, {"name", form->GetName()}};
        } catch (...) {
            return {{"error", "Invalid FormID: " + formIdHex}};
        }
    }

    namespace {
        // Vanilla equip slots (Skyrim.esm). Looked up by form ID instead of through
        // BGSDefaultObjectManager, whose table layout changed in 1.7.99.
        constexpr RE::FormID kRightHandSlot = 0x00013F42;
        constexpr RE::FormID kLeftHandSlot = 0x00013F43;

        const RE::BGSEquipSlot* HandSlot(const std::string& hand) {
            if (hand == "left") return RE::TESForm::LookupByID<RE::BGSEquipSlot>(kLeftHandSlot);
            if (hand == "right") return RE::TESForm::LookupByID<RE::BGSEquipSlot>(kRightHandSlot);
            return nullptr;  // let the game choose
        }

        std::string DisplayName(RE::TESForm* form) {
            const char* name = form->GetName();
            return (name && name[0]) ? name : std::format("{:08X}", form->GetFormID());
        }

        // The inventory entry's extra data carries enchantments, tempering and custom names,
        // so equip the copy the actor actually has rather than a plain base item. When
        // unequipping, prefer the copy that is worn.
        RE::ExtraDataList* PickExtraList(RE::InventoryEntryData* entry, bool wantWorn) {
            if (!entry || !entry->extraLists) return nullptr;
            RE::ExtraDataList* first = nullptr;
            for (auto* xList : *entry->extraLists) {
                if (!xList) continue;
                if (!first) first = xList;
                const bool worn = xList->HasType(RE::ExtraDataType::kWorn) || xList->HasType(RE::ExtraDataType::kWornLeft);
                if (worn == wantWorn) return xList;
            }
            return wantWorn ? first : nullptr;
        }

        json EquipOne(RE::Actor* actor, RE::ActorEquipManager* equipManager,
                      RE::TESObjectREFR::InventoryItemMap& inventory, const json& item, bool unequip) {
            json r;
            if (!item.is_string() && !item.is_object()) return json{{"ok", false}, {"error", "Each item is a form ID string or {formId, hand}"}};
            const std::string formIdHex = item.is_string() ? item.get<std::string>() : item.value("formId", "");
            const std::string hand = item.is_object() ? item.value("hand", "") : "";
            r["formId"] = formIdHex;
            if (!hand.empty()) r["hand"] = hand;

            auto fail = [&](const std::string& message) {
                r["ok"] = false;
                r["error"] = message;
                return r;
            };

            if (formIdHex.empty()) return fail("Missing formId");
            if (!hand.empty() && hand != "left" && hand != "right" && hand != "both")
                return fail("hand must be left, right or both");

            RE::TESForm* form = nullptr;
            try {
                form = RE::TESForm::LookupByID(Helpers::ParseFormId(formIdHex));
            } catch (...) {
                return fail("Invalid form ID");
            }
            if (!form) return fail("Form not found");
            r["name"] = DisplayName(form);

            if (auto* spell = form->As<RE::SpellItem>()) {
                if (unequip) return fail("Unequipping a spell is not supported yet: equip another spell or a weapon in that hand instead");
                if (hand == "both") {
                    equipManager->EquipSpell(actor, spell, HandSlot("left"));
                    equipManager->EquipSpell(actor, spell, HandSlot("right"));
                } else {
                    equipManager->EquipSpell(actor, spell, HandSlot(hand.empty() ? "right" : hand));
                }
                r["type"] = "spell";
                r["ok"] = true;
                return r;
            }

            if (auto* shout = form->As<RE::TESShout>()) {
                if (unequip) return fail("Unequipping a shout is not supported yet: equip another shout or power instead");
                equipManager->EquipShout(actor, shout);
                r["type"] = "shout";
                r["ok"] = true;
                return r;
            }

            auto* object = form->As<RE::TESBoundObject>();
            if (!object) return fail("Not an item, spell or shout");

            auto it = inventory.find(object);
            const auto count = it != inventory.end() ? it->second.first : 0;
            if (count <= 0) return fail("Not in the inventory: add it first (add_item)");

            const RE::BGSEquipSlot* slot = nullptr;
            if (form->Is(RE::FormType::Weapon) && (hand == "left" || hand == "right")) slot = HandSlot(hand);
            if (form->Is(RE::FormType::Weapon) && hand == "both") return fail("hand 'both' applies to spells; a weapon goes in one hand");

            auto* extra = PickExtraList(it->second.second.get(), unequip);
            // Arrows and bolts equip as a stack.
            const std::uint32_t equipCount = form->Is(RE::FormType::Ammo) ? static_cast<std::uint32_t>(count) : 1u;

            if (unequip) {
                equipManager->UnequipObject(actor, object, extra, equipCount, slot);
            } else {
                equipManager->EquipObject(actor, object, extra, equipCount, slot);
                if (!Helpers::ArmorShowsOn(form->As<RE::TESObjectARMO>(), actor)) {
                    r["showsOnRace"] = false;
                    r["warning"] = "Worn, but it has no model for this character's race and sex, so nothing shows (made for another race).";
                }
            }
            r["ok"] = true;
            return r;
        }
    }

    // FEAT-205: equip or unequip several items, spells or shouts in one call, on the player
    // or on an NPC. Each entry is a form ID string or {"formId", "hand": left|right|both}.
    // The game applies equips on its next update, so read the result back with
    // get_equipped_items rather than trusting "ok" alone.
    json EquipItems(const json& items, const std::string& refId, bool unequip) {
        if (!items.is_array() || items.empty()) return {{"error", "items must be a non-empty list"}};

        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};

        auto* equipManager = RE::ActorEquipManager::GetSingleton();
        if (!equipManager) return {{"error", "Equip manager not available"}};

        // One inventory pass for the whole list, limited to the requested items.
        std::unordered_set<RE::FormID> wanted;
        for (const auto& item : items) {
            try {
                const std::string id = item.is_string() ? item.get<std::string>()
                                     : item.is_object() ? item.value("formId", "") : "";
                if (!id.empty()) wanted.insert(Helpers::ParseFormId(id));
            } catch (...) {}
        }
        auto inventory = actor->GetInventory([&](RE::TESBoundObject& obj) { return wanted.contains(obj.GetFormID()); });

        json results = json::array();
        int succeeded = 0;
        for (const auto& item : items) {
            auto r = EquipOne(actor, equipManager, inventory, item, unequip);
            if (r.value("ok", false)) ++succeeded;
            results.push_back(std::move(r));
        }

        return {
            {"actor", actor->IsPlayerRef() ? "player" : std::format("{:08X}", actor->GetFormID())},
            {"action", unequip ? "unequip" : "equip"},
            {"succeeded", succeeded},
            {"failed", static_cast<int>(results.size()) - succeeded},
            {"results", results}
        };
    }

    namespace {
        // The single-item actions keep their 1.1 shape: an error, or the item that changed.
        json SingleResult(json batch, const char* doneKey) {
            if (batch.contains("error")) return batch;
            auto r = batch["results"][0];
            if (!r.value("ok", false)) return {{"error", r.value("error", std::string("Failed"))}};
            r.erase("ok");
            r[doneKey] = true;
            return r;
        }
    }

    json EquipItem(const std::string& formIdHex) {
        return SingleResult(EquipItems(json::array({formIdHex}), "", false), "equipped");
    }

    json UnequipItem(const std::string& formIdHex) {
        return SingleResult(EquipItems(json::array({formIdHex}), "", true), "unequipped");
    }

}

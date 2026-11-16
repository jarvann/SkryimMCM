#include "LootManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <format>
#include <set>
#include <string>
#include <unordered_map>
#include <vector>

namespace SkyrimMCP::LootManager {

    using json = nlohmann::json;

    namespace {
        constexpr RE::FormID kGold = 0x0000000F;
        // Skyrim.esm vendor keywords, by form ID because editor IDs are often not loaded.
        constexpr RE::FormID kVendorItemOreIngot = 0x000914EC;
        constexpr RE::FormID kVendorItemAnimalHide = 0x000914EA;
        constexpr RE::FormID kVendorItemGem = 0x000914ED;
        constexpr RE::FormID kVendorItemJewelry = 0x0008F95A;

        const std::set<std::string> kCategories = {
            "all", "weapons", "armor", "jewelry", "ammo", "potions", "poisons", "food", "ingredients", "books",
            "scrolls", "soulgems", "gems", "smithing", "keys", "misc", "gold", "valuables",
        };

        std::string Lower(std::string s) {
            std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            return s;
        }

        bool HasKeyword(RE::TESBoundObject* object, RE::FormID keyword) {
            auto* keywords = object->As<RE::BGSKeywordForm>();
            return keywords && keywords->HasKeywordID(keyword);
        }

        // The categories one item belongs to ("valuables" is decided by value later).
        std::set<std::string> CategoriesOf(RE::TESBoundObject* object) {
            std::set<std::string> c;
            if (object->GetFormID() == kGold) { c.insert("gold"); return c; }
            switch (object->GetFormType()) {
                case RE::FormType::Weapon: c.insert("weapons"); break;
                case RE::FormType::Armor:
                    c.insert(HasKeyword(object, kVendorItemJewelry) ? "jewelry" : "armor");
                    break;
                case RE::FormType::Ammo: c.insert("ammo"); break;
                case RE::FormType::AlchemyItem:
                    if (auto* alch = object->As<RE::AlchemyItem>()) {
                        c.insert(alch->IsFood() ? "food" : alch->IsPoison() ? "poisons" : "potions");
                    }
                    break;
                case RE::FormType::Ingredient: c.insert("ingredients"); break;
                case RE::FormType::Book: c.insert("books"); break;
                case RE::FormType::Scroll: c.insert("scrolls"); break;
                case RE::FormType::SoulGem: c.insert("soulgems"); break;
                case RE::FormType::KeyMaster: c.insert("keys"); break;
                case RE::FormType::Misc:
                    if (HasKeyword(object, kVendorItemOreIngot) || HasKeyword(object, kVendorItemAnimalHide)) c.insert("smithing");
                    if (HasKeyword(object, kVendorItemGem)) c.insert("gems");
                    c.insert("misc");
                    break;
                default: break;
            }
            return c;
        }

        // Hidden items the player can't take: skins, AI tokens, non-playable armor and weapons.
        // They have no name and a gold value of -1, and the game's own loot menu never shows them.
        bool Lootable(RE::TESBoundObject* object) {
            if (object->GetFormID() == kGold) return true;
            const char* name = object->GetName();
            if (!name || !name[0]) return false;
            if (object->GetGoldValue() < 0) return false;
            switch (object->GetFormType()) {
                case RE::FormType::Armor:
                    return (object->GetFormFlags() & RE::TESObjectARMO::RecordFlags::kNonPlayable) == 0;
                case RE::FormType::Weapon:
                case RE::FormType::Ammo:
                    return object->GetPlayable();
                default:
                    return true;
            }
        }

        RE::TESObjectREFR* ResolveRef(const std::string& id, std::string& error) {
            if (id.empty() || id == "player") return RE::PlayerCharacter::GetSingleton();
            // With a container open (looting, a chest, a follower's inventory), "crosshair" and
            // "container" mean that container: the menu hides the crosshair target.
            if (id == "crosshair" || id == "container") {
                auto* ui = RE::UI::GetSingleton();
                if (ui && ui->IsMenuOpen(RE::ContainerMenu::MENU_NAME)) {
                    if (auto open = RE::TESObjectREFR::LookupByHandle(RE::ContainerMenu::GetTargetRefHandle())) return open.get();
                }
                if (id == "container") { error = "No container menu is open"; return nullptr; }
                auto* pick = RE::CrosshairPickData::GetSingleton();
                auto target = pick ? pick->GetActiveTarget().get() : RE::NiPointer<RE::TESObjectREFR>();
                if (!target) { error = "Nothing is under the crosshair"; return nullptr; }
                return target.get();
            }
            try {
                auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(id));
                if (auto* ref = form ? form->As<RE::TESObjectREFR>() : nullptr) return ref;
            } catch (...) {}
            error = "Reference not found: " + id;
            return nullptr;
        }

        std::string RefLabel(RE::TESObjectREFR* ref) {
            if (ref->IsPlayerRef()) return "player";
            const char* name = ref->GetName();
            return std::format("{} ({:08X})", (name && name[0]) ? name : "unnamed", ref->GetFormID());
        }

        struct Filter {
            std::set<std::string> categories;
            int minValue = 0;
            bool includeEquipped = false;
            bool includeFavorites = false;
            bool includeQuestItems = false;
            bool excludeGold = false;  // LootBodies includeGold=false: wins over every category
            // TransferItems "items": exact forms to move, with a count (-1 = all). When set, it
            // replaces the category match.
            std::unordered_map<RE::FormID, int> items;
        };

        bool ReadFilter(const json& params, Filter& f, std::string& error) {
            auto cats = params.value("categories", json::array());
            if (cats.is_string()) cats = json::array({cats});
            for (const auto& c : cats) {
                if (!c.is_string()) continue;
                auto name = Lower(c.get<std::string>());
                if (!kCategories.contains(name)) {
                    error = "Unknown category '" + name + "'. Use: all, weapons, armor, jewelry, ammo, potions, poisons, food, "
                            "ingredients, books, scrolls, soulgems, gems, smithing, keys, misc, gold, valuables";
                    return false;
                }
                f.categories.insert(name);
            }
            if (f.categories.empty() && !params.contains("items")) { error = "categories or items is required (for example [\"smithing\"] or [\"all\"])"; return false; }
            f.minValue = params.value("minValue", 0);
            f.includeEquipped = params.value("includeEquipped", false);
            f.includeFavorites = params.value("includeFavorites", false);
            f.includeQuestItems = params.value("includeQuestItems", false);
            return true;
        }

        bool Matches(const Filter& f, RE::TESBoundObject* object) {
            if (!f.items.empty()) return f.items.contains(object->GetFormID());
            if (f.categories.contains("all")) return true;
            auto cats = CategoriesOf(object);
            for (const auto& c : cats) if (f.categories.contains(c)) return true;
            // "valuables": anything worth at least minValue (default 100) per item, plus gold.
            if (f.categories.contains("valuables")) {
                if (object->GetFormID() == kGold) return true;
                return object->GetGoldValue() >= std::max(f.minValue, 100);
            }
            return false;
        }

        // Moves every matching stack from one reference to another. Stacks with extra data
        // (enchantments, tempering, names) move with it, so nothing is flattened.
        json MoveMatching(RE::TESObjectREFR* from, RE::TESObjectREFR* to, const Filter& f,
                          RE::ITEM_REMOVE_REASON reason, bool dryRun, int& movedStacks, int& movedValue) {
            json moved = json::array();
            auto inventory = from->GetInventory();
            for (auto& [object, data] : inventory) {
                auto count = data.first;
                auto& entry = data.second;
                if (!object || count <= 0 || !entry) continue;
                if (!Lootable(object)) continue;
                if (f.excludeGold && object->GetFormID() == kGold) continue;
                if (!Matches(f, object)) continue;
                if (f.minValue > 0 && object->GetFormID() != kGold && object->GetGoldValue() < f.minValue) continue;
                if (!f.includeQuestItems && entry->IsQuestObject()) continue;
                if (!f.includeEquipped && entry->IsWorn()) continue;
                if (!f.includeFavorites && entry->IsFavorited()) continue;

                // A named item moves only as many as asked for (and keeps its other copies).
                if (!f.items.empty()) {
                    const int wanted = f.items.at(object->GetFormID());
                    if (wanted > 0) count = std::min(count, wanted);
                }
                const char* name = object->GetName();
                json m;
                m["formId"] = std::format("{:08X}", object->GetFormID());
                m["name"] = (name && name[0]) ? name : m["formId"].get<std::string>();
                const int itemValue = std::max(0, object->GetGoldValue()) * count;
                m["count"] = count;
                m["value"] = itemValue;
                if (entry->IsWorn()) m["equipped"] = true;
                movedValue += itemValue;
                ++movedStacks;
                moved.push_back(m);
                if (dryRun) continue;

                // Stacks that carry extra data first, then the plain remainder.
                std::int32_t remaining = count;
                std::vector<RE::ExtraDataList*> lists;
                if (entry->extraLists) {
                    for (auto* xList : *entry->extraLists) if (xList) lists.push_back(xList);
                }
                for (auto* xList : lists) {
                    const auto n = std::min(remaining, std::max(1, xList->GetCount()));
                    if (n <= 0) break;
                    from->RemoveItem(object, n, reason, xList, to);
                    remaining -= n;
                }
                if (remaining > 0) from->RemoveItem(object, remaining, reason, nullptr, to);
            }
            return moved;
        }
    }

    namespace {
        std::unordered_map<RE::FormID, int> Counts(RE::TESObjectREFR* ref) {
            std::unordered_map<RE::FormID, int> counts;
            for (auto& [object, data] : ref->GetInventory()) {
                if (object && data.first > 0) counts[object->GetFormID()] += data.first;
            }
            return counts;
        }

        // TransferItems "items": [{"item": FormID or name, "count": n}] or plain strings. Names are
        // matched against what the source actually holds: exact (case-insensitive) first, then a
        // unique partial match; an ambiguous name is an error listing the candidates.
        bool ResolveItems(const json& list, RE::TESObjectREFR* from, Filter& f, json& unresolved, std::string& error) {
            std::vector<std::pair<RE::TESBoundObject*, std::string>> held;
            for (auto& [object, data] : from->GetInventory()) {
                if (!object || data.first <= 0) continue;
                const char* name = object->GetName();
                held.emplace_back(object, Lower((name && name[0]) ? name : ""));
            }
            for (const auto& entry : list) {
                std::string key;
                int count = -1;
                if (entry.is_string()) key = entry.get<std::string>();
                else if (entry.is_object()) {
                    key = entry.value("item", entry.value("formId", entry.value("name", "")));
                    count = entry.value("count", -1);
                }
                if (key.empty()) continue;

                RE::TESBoundObject* found = nullptr;
                try {
                    const auto id = Helpers::ParseFormId(key);
                    for (auto& [object, name] : held) if (object->GetFormID() == id) { found = object; break; }
                } catch (...) {}
                if (!found) {
                    const auto wanted = Lower(key);
                    std::vector<RE::TESBoundObject*> partial;
                    for (auto& [object, name] : held) {
                        if (name == wanted) { found = object; break; }
                        if (!wanted.empty() && name.find(wanted) != std::string::npos) partial.push_back(object);
                    }
                    if (!found && partial.size() == 1) found = partial[0];
                    if (!found && partial.size() > 1) {
                        json names = json::array();
                        for (auto* o : partial) names.push_back(std::format("{} ({:08X})", o->GetName(), o->GetFormID()));
                        error = "'" + key + "' matches several items; use the FormID: " + names.dump();
                        return false;
                    }
                }
                if (!found) { unresolved.push_back(key); continue; }
                f.items[found->GetFormID()] = count;
            }
            return true;
        }
    }

    json TransferItems(const json& params) {
        std::string error;
        auto* from = ResolveRef(params.value("from", "player"), error);
        if (!from) return {{"error", error}};
        auto* to = ResolveRef(params.value("to", "crosshair"), error);
        if (!to) return {{"error", error}};
        if (from == to) return {{"error", "from and to are the same reference"}};

        Filter f;
        if (!ReadFilter(params, f, error)) return {{"error", error}};
        json unresolved = json::array();
        if (params.contains("items")) {
            auto list = params["items"];
            if (list.is_string()) list = json::array({list});
            if (!ResolveItems(list, from, f, unresolved, error)) return {{"error", error}};
            if (f.items.empty()) return {{"error", RefLabel(from) + " has none of the named items"}, {"notFound", unresolved}};
        }
        const bool dryRun = params.value("dryRun", false);
        const bool allowStealing = params.value("allowStealing", false);

        // Taking from something another NPC or faction owns is stealing. Only with consent,
        // and then the game's own steal handling applies (bounty if seen).
        auto reason = RE::ITEM_REMOVE_REASON::kStoreInContainer;
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (to == player && from != player) {
            auto* owner = from->GetOwner();
            const bool ownedByOther = owner && owner != player->GetActorBase() && !from->IsDead(true);
            if (ownedByOther) {
                if (!allowStealing) {
                    return {{"error", RefLabel(from) + " belongs to someone else, so taking from it is stealing. "
                                      "Ask the player first, then call again with allowStealing=true."}};
                }
                reason = RE::ITEM_REMOVE_REASON::kSteal;
            }
        }
        if (auto* actor = from->As<RE::Actor>(); actor && !actor->IsDead() && !actor->IsPlayerRef() && !allowStealing) {
            return {{"error", RefLabel(from) + " is a living NPC: taking their items is pickpocketing. Use allowStealing=true only if the player asks."}};
        }

        // What the destination holds before and after, so a top-up of an existing pile is visible.
        const auto destBefore = Counts(to);
        int stacks = 0, value = 0;
        auto moved = MoveMatching(from, to, f, reason, dryRun, stacks, value);
        const auto destAfter = dryRun ? destBefore : Counts(to);
        int alreadyHeldStacks = 0;
        for (auto& m : moved) {
            RE::FormID id = 0;
            try { id = Helpers::ParseFormId(m["formId"].get<std::string>()); } catch (...) {}
            const int before = destBefore.contains(id) ? destBefore.at(id) : 0;
            m["destinationBefore"] = before;
            m["destinationAfter"] = dryRun ? before + m["count"].get<int>() : (destAfter.contains(id) ? destAfter.at(id) : 0);
            if (before > 0) ++alreadyHeldStacks;
        }

        json result = {
            {"from", RefLabel(from)}, {"to", RefLabel(to)}, {"dryRun", dryRun},
            {"stacks", stacks}, {"totalValue", value}, {"items", moved},
            {"stolen", reason == RE::ITEM_REMOVE_REASON::kSteal},
            {"destinationStacksBefore", destBefore.size()},
            {"destinationAlreadyHadSome", alreadyHeldStacks},
        };
        if (alreadyHeldStacks > 0) {
            result["destinationNote"] = std::format("{} of these {} already in {}: the move tops up those piles (see destinationBefore).",
                                                    alreadyHeldStacks, alreadyHeldStacks == 1 ? "item was" : "items were", RefLabel(to));
        }
        if (!unresolved.empty()) result["notFound"] = unresolved;
        // Named items the filters held back (worn, favourite, quest item).
        if (!f.items.empty()) {
            json heldBack = json::array();
            for (auto& [id, n] : f.items) {
                const bool wasMoved = std::any_of(moved.begin(), moved.end(), [&](const json& m) {
                    try { return Helpers::ParseFormId(m["formId"].get<std::string>()) == id; } catch (...) { return false; }
                });
                if (!wasMoved) heldBack.push_back(std::format("{:08X}", id));
            }
            if (!heldBack.empty()) {
                // Name and the reason for each, from the source's inventory entry.
                json detailed = json::array();
                for (auto& [object, data] : from->GetInventory()) {
                    if (!object) continue;
                    const auto idText = std::format("{:08X}", object->GetFormID());
                    if (std::find(heldBack.begin(), heldBack.end(), idText) == heldBack.end()) continue;
                    json h = {{"formId", idText}, {"name", object->GetName() ? object->GetName() : ""}};
                    auto& entry = data.second;
                    std::string why;
                    if (entry && entry->IsWorn() && !f.includeEquipped) why = "worn (includeEquipped=true moves it)";
                    else if (entry && entry->IsFavorited() && !f.includeFavorites) why = "favourite (includeFavorites=true moves it)";
                    else if (entry && entry->IsQuestObject() && !f.includeQuestItems) why = "quest item (includeQuestItems=true moves it)";
                    else if (!Lootable(object)) why = "hidden item (can't be moved)";
                    else if (f.minValue > 0 && object->GetGoldValue() < f.minValue) why = "below minValue";
                    else why = "not moved";
                    h["reason"] = why;
                    detailed.push_back(h);
                }
                result["heldBack"] = detailed;
            }
        }
        return result;
    }

    json LootBodies(const json& params) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* processLists = RE::ProcessLists::GetSingleton();
        if (!player || !processLists) return {{"error", "Game not ready"}};

        json filterParams = params;
        if (!filterParams.contains("categories")) filterParams["categories"] = json::array({"valuables"});
        Filter f;
        std::string error;
        if (!ReadFilter(filterParams, f, error)) return {{"error", error}};
        // includeGold=false keeps gold out even with "all" or "valuables" (both match gold).
        if (params.value("includeGold", true)) f.categories.insert("gold");
        else f.excludeGold = true;
        // A body's worn armor, jewelry and weapons are normal loot (the game's own loot menu
        // shows them), so they are offered unless the caller says otherwise.
        f.includeEquipped = params.value("includeEquipped", true);
        const float radius = params.value("radius", 2048.0f);
        const bool dryRun = params.value("dryRun", false);
        const auto playerPos = player->GetPosition();

        json bodies = json::array();
        int stacks = 0, value = 0, inRange = 0;
        for (auto& handle : processLists->highActorHandles) {
            auto actorPtr = handle.get();
            auto* actor = actorPtr.get();
            if (!actor || actor == player || !actor->IsDead()) continue;
            if (actor->GetPosition().GetDistance(playerPos) > radius) continue;
            // Followers and essential NPCs who are only "dead" for a moment are left alone.
            if (actor->IsPlayerTeammate()) continue;
            ++inRange;

            int bodyStacks = 0, bodyValue = 0;
            auto moved = MoveMatching(actor, player, f, RE::ITEM_REMOVE_REASON::kStoreInContainer, dryRun, bodyStacks, bodyValue);
            if (moved.empty()) continue;
            stacks += bodyStacks;
            value += bodyValue;
            bodies.push_back({{"body", RefLabel(actor)}, {"items", moved}, {"value", bodyValue}});
        }

        json reply = {
            {"dryRun", dryRun}, {"radius", radius},
            {"bodiesInRange", inRange},                        // dead, loaded, not a follower
            {"bodiesWithLoot", bodies.size()},                 // of those, bodies with something matching
            {"bodiesLooted", dryRun ? 0 : static_cast<int>(bodies.size())},
            {"stacks", stacks}, {"totalValue", value}, {"bodies", bodies},
        };
        if (inRange == 0) {
            reply["message"] = std::format("No dead bodies within {:.0f} units. Only bodies in loaded cells near the player are searched.", radius);
        } else if (bodies.empty()) {
            reply["message"] = std::format("{} bod{} in range, but nothing on {} matches the filter.", inRange, inRange == 1 ? "y" : "ies",
                                           inRange == 1 ? "it" : "them");
        }
        return reply;
    }

}

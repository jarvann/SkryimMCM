#include "PlayerQueries.h"
#include "Helpers.h"
#include "NPCManager.h"
#include "CombatAnalysis.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <format>
#include <unordered_set>

namespace SkyrimMCP::PlayerQueries {

    using json = nlohmann::json;

    json GetPlayerInfo() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* base = player->GetActorBase();
        auto pos = player->GetPosition();
        auto* cell = player->GetParentCell();
        auto* worldspace = player->GetWorldspace();
        auto* avo = player->AsActorValueOwner();

        json result;
        result["name"] = base ? base->GetName() : "unknown";
        result["race"] = base && base->GetRace() ? base->GetRace()->GetName() : "unknown";
        result["level"] = player->GetLevel();

        if (avo) {
            result["health"] = avo->GetActorValue(RE::ActorValue::kHealth);
            result["healthMax"] = avo->GetBaseActorValue(RE::ActorValue::kHealth);
            result["magicka"] = avo->GetActorValue(RE::ActorValue::kMagicka);
            result["magickaMax"] = avo->GetBaseActorValue(RE::ActorValue::kMagicka);
            result["stamina"] = avo->GetActorValue(RE::ActorValue::kStamina);
            result["staminaMax"] = avo->GetBaseActorValue(RE::ActorValue::kStamina);
        }

        result["posX"] = pos.x;
        result["posY"] = pos.y;
        result["posZ"] = pos.z;
        result["cellName"] = cell ? cell->GetName() : "unknown";
        result["worldspaceName"] = worldspace ? worldspace->GetName() : "interior";

        return result;
    }

    json GetInventory(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", refId.empty() ? "Player not available" : "Actor not found: " + refId}};

        auto inv = actor->GetInventory();
        json items = json::array();

        for (auto& [form, data] : inv) {
            if (!form || data.first <= 0) continue;

            try {
                json item;
                item["formId"] = std::format("{:08X}", form->GetFormID());
                item["name"] = form->GetName();
                item["count"] = data.first;

                // Get display name (custom renamed items) and enchantments from InventoryEntryData
                auto& entryData = data.second;
                if (entryData) {
                    const char* displayName = entryData->GetDisplayName();
                    if (displayName && displayName[0] && std::strcmp(displayName, form->GetName()) != 0) {
                        item["displayName"] = displayName;
                    }

                    // Get enchantment
                    auto* enchantment = entryData->GetEnchantment();
                    if (enchantment) {
                        json ench;
                        ench["name"] = enchantment->GetName();
                        ench["formId"] = std::format("{:08X}", enchantment->GetFormID());

                        json effects = json::array();
                        for (auto* effect : enchantment->effects) {
                            if (!effect || !effect->baseEffect) continue;
                            json e;
                            e["name"] = effect->baseEffect->GetName();
                            e["magnitude"] = effect->effectItem.magnitude;
                            e["duration"] = effect->effectItem.duration;
                            e["area"] = effect->effectItem.area;
                            effects.push_back(e);
                        }
                        ench["effects"] = effects;
                        item["enchantment"] = ench;
                    }

                    // Get enchantment charge
                    auto charge = entryData->GetEnchantmentCharge();
                    if (charge.has_value()) {
                        item["enchantmentCharge"] = charge.value();
                    }
                }

                switch (form->GetFormType()) {
                    case RE::FormType::Weapon: item["type"] = "weapon"; break;
                    case RE::FormType::Armor: item["type"] = "armor"; break;
                    case RE::FormType::AlchemyItem: item["type"] = "potion"; break;
                    case RE::FormType::Ingredient: item["type"] = "ingredient"; break;
                    case RE::FormType::Book: item["type"] = "book"; break;
                    case RE::FormType::Misc: item["type"] = "misc"; break;
                    case RE::FormType::Ammo: item["type"] = "ammo"; break;
                    case RE::FormType::SoulGem: item["type"] = "soulgem"; break;
                    case RE::FormType::KeyMaster: item["type"] = "key"; break;
                    case RE::FormType::Scroll: item["type"] = "scroll"; break;
                    default: item["type"] = "other"; break;
                }

                if (auto* boundObj = form->As<RE::TESBoundObject>()) {
                    item["weight"] = boundObj->GetWeight();
                    item["value"] = boundObj->GetGoldValue();
                }

                items.push_back(item);
            } catch (...) {
                continue;
            }
        }

        json result;
        result["items"] = items;
        if (!refId.empty() && refId != "player") {
            result["refId"] = refId;
            result["name"] = actor->GetName() ? actor->GetName() : "";
            result["count"] = items.size();
        }
        return result;
    }

    json GetGoldCount() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* gold = RE::TESForm::LookupByID<RE::TESBoundObject>(0x0000000F);
        int count = 0;
        if (gold) {
            auto inv = player->GetInventory([gold](const RE::TESBoundObject& obj) {
                return obj.GetFormID() == gold->GetFormID();
            });
            for (auto& [form, data] : inv) {
                count += data.first;
            }
        }

        return {{"gold", count}};
    }

    json GetActiveEffects() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* magicTarget = player->AsMagicTarget();
        if (!magicTarget) return {{"error", "Magic target not available"}};

        auto* effectList = magicTarget->GetActiveEffectList();
        json effects = json::array();

        if (effectList) {
            for (auto& activeEffect : *effectList) {
                if (!activeEffect) continue;
                auto* baseEffect = activeEffect->GetBaseObject();

                json e;
                e["name"] = baseEffect ? baseEffect->GetName() : "unknown";
                if (baseEffect) {
                    e["formId"] = std::format("{:08X}", baseEffect->GetFormID());
                }
                e["magnitude"] = activeEffect->magnitude;
                e["duration"] = activeEffect->duration;
                e["elapsed"] = activeEffect->elapsedSeconds;
                effects.push_back(e);
            }
        }

        return {{"effects", effects}};
    }

    json GetEquippedItems() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        json equipped = json::array();
        std::unordered_set<RE::FormID> seen;  // avoid duplicates (items span multiple slots)

        // Named slots for human-readable output
        static const std::pair<const char*, RE::BGSBipedObjectForm::BipedObjectSlot> namedSlots[] = {
            {"head",      RE::BGSBipedObjectForm::BipedObjectSlot::kHead},
            {"hair",      RE::BGSBipedObjectForm::BipedObjectSlot::kHair},
            {"body",      RE::BGSBipedObjectForm::BipedObjectSlot::kBody},
            {"hands",     RE::BGSBipedObjectForm::BipedObjectSlot::kHands},
            {"forearms",  RE::BGSBipedObjectForm::BipedObjectSlot::kForearms},
            {"amulet",    RE::BGSBipedObjectForm::BipedObjectSlot::kAmulet},
            {"ring",      RE::BGSBipedObjectForm::BipedObjectSlot::kRing},
            {"feet",      RE::BGSBipedObjectForm::BipedObjectSlot::kFeet},
            {"calves",    RE::BGSBipedObjectForm::BipedObjectSlot::kCalves},
            {"shield",    RE::BGSBipedObjectForm::BipedObjectSlot::kShield},
            {"tail",      RE::BGSBipedObjectForm::BipedObjectSlot::kTail},
            {"circlet",   RE::BGSBipedObjectForm::BipedObjectSlot::kCirclet},
        };

        auto addItem = [&](const char* slotName, RE::TESObjectARMO* item) {
            if (!item || seen.count(item->GetFormID())) return;
            seen.insert(item->GetFormID());
            json e;
            e["slot"] = slotName;
            e["slotMask"] = item->GetSlotMask().underlying();
            e["formId"] = std::format("{:08X}", item->GetFormID());
            e["name"] = item->GetName();
            equipped.push_back(e);
        };

        // Check all named slots
        for (auto& [name, slot] : namedSlots) {
            auto* item = player->GetWornArmor(slot);
            addItem(name, item);
        }

        // Check all 32 unnamed/mod slots (bits 11-31)
        for (int bit = 11; bit < 32; bit++) {
            if (bit == 12) continue;  // kCirclet already checked
            auto slot = static_cast<RE::BGSBipedObjectForm::BipedObjectSlot>(1 << bit);
            auto* item = player->GetWornArmor(slot);
            if (item) {
                std::string slotName = std::format("slot{}", 30 + bit);  // Skyrim slot numbering: bit 0 = slot 30
                addItem(slotName.c_str(), item);
            }
        }

        // Wielded weapons/spells
        auto* rightHand = player->GetEquippedObject(false);
        if (rightHand) {
            json e;
            e["slot"] = "rightHand";
            e["formId"] = std::format("{:08X}", rightHand->GetFormID());
            e["name"] = rightHand->GetName();
            equipped.push_back(e);
        }

        auto* leftHand = player->GetEquippedObject(true);
        if (leftHand) {
            json e;
            e["slot"] = "leftHand";
            e["formId"] = std::format("{:08X}", leftHand->GetFormID());
            e["name"] = leftHand->GetName();
            equipped.push_back(e);
        }

        return {{"equipped", equipped}};
    }

    json GetKnownSpells() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        json spells = json::array();

        // Use VisitSpells to get ALL known spells (base + runtime-learned)
        class SpellCollector : public RE::Actor::ForEachSpellVisitor {
        public:
            json& spells;
            SpellCollector(json& s) : spells(s) {}

            RE::BSContainer::ForEachResult Visit(RE::SpellItem* spell) override {
                if (!spell) return RE::BSContainer::ForEachResult::kContinue;
                try {
                    json s;
                    s["formId"] = std::format("{:08X}", spell->GetFormID());
                    s["name"] = spell->GetName();
                    s["type"] = Helpers::SpellTypeToString(spell->GetSpellType());

                    auto* costliestEffect = spell->GetCostliestEffectItem();
                    if (costliestEffect && costliestEffect->baseEffect) {
                        s["school"] = Helpers::ActorValueToSchool(costliestEffect->baseEffect->GetMagickSkill());
                        s["magnitude"] = costliestEffect->effectItem.magnitude;
                        s["duration"] = costliestEffect->effectItem.duration;
                    }

                    spells.push_back(s);
                } catch (...) {}
                return RE::BSContainer::ForEachResult::kContinue;
            }
        };

        SpellCollector collector(spells);
        player->VisitSpells(collector);

        return {{"spells", spells}, {"count", spells.size()}};
    }

    json GetKnownShouts() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* base = player->GetActorBase();
        if (!base) return {{"error", "Actor base not available"}};

        auto* spellData = base->GetSpellList();
        if (!spellData) return {{"error", "Spell list not available"}};

        json shouts = json::array();

        for (std::uint32_t i = 0; i < spellData->numShouts; i++) {
            auto* shout = spellData->shouts[i];
            if (!shout) continue;

            try {
                json s;
                s["formId"] = std::format("{:08X}", shout->GetFormID());
                s["name"] = shout->GetName();
                s["known"] = shout->GetKnown();

                // List the three words
                json words = json::array();
                for (int w = 0; w < 3; w++) {
                    auto& variation = shout->variations[w];
                    json word;
                    if (variation.word) {
                        word["name"] = variation.word->GetName();
                        word["formId"] = std::format("{:08X}", variation.word->GetFormID());
                    }
                    if (variation.spell) {
                        word["spellName"] = variation.spell->GetName();
                    }
                    word["recoveryTime"] = variation.recoveryTime;
                    words.push_back(word);
                }
                s["words"] = words;

                shouts.push_back(s);
            } catch (...) {
                continue;
            }
        }

        return {{"shouts", shouts}, {"count", shouts.size()}};
    }

    json GetSkillLevels() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* avo = player->AsActorValueOwner();
        if (!avo) return {{"error", "Actor value owner not available"}};

        struct SkillEntry { const char* name; RE::ActorValue av; };
        static const SkillEntry skills[] = {
            {"oneHanded", RE::ActorValue::kOneHanded},
            {"twoHanded", RE::ActorValue::kTwoHanded},
            {"archery", RE::ActorValue::kArchery},
            {"block", RE::ActorValue::kBlock},
            {"smithing", RE::ActorValue::kSmithing},
            {"heavyArmor", RE::ActorValue::kHeavyArmor},
            {"lightArmor", RE::ActorValue::kLightArmor},
            {"pickpocket", RE::ActorValue::kPickpocket},
            {"lockpicking", RE::ActorValue::kLockpicking},
            {"sneak", RE::ActorValue::kSneak},
            {"alchemy", RE::ActorValue::kAlchemy},
            {"speech", RE::ActorValue::kSpeech},
            {"alteration", RE::ActorValue::kAlteration},
            {"conjuration", RE::ActorValue::kConjuration},
            {"destruction", RE::ActorValue::kDestruction},
            {"illusion", RE::ActorValue::kIllusion},
            {"restoration", RE::ActorValue::kRestoration},
            {"enchanting", RE::ActorValue::kEnchanting},
        };

        json result;
        json skillList = json::array();

        for (auto& [name, av] : skills) {
            json s;
            s["name"] = name;
            s["level"] = avo->GetActorValue(av);
            s["baseLevel"] = avo->GetBaseActorValue(av);
            skillList.push_back(s);
        }

        result["skills"] = skillList;
        result["playerLevel"] = player->GetLevel();
        return result;
    }

    json GetPerks() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        json perks = json::array();

        // Get runtime-added perks (everything gained during gameplay)
        try {
            auto& runtimeData = player->GetPlayerRuntimeData();
            for (auto* perkData : runtimeData.addedPerks) {
                if (!perkData || !perkData->perk) continue;
                try {
                    json p;
                    p["formId"] = std::format("{:08X}", perkData->perk->GetFormID());
                    p["name"] = perkData->perk->GetName();
                    p["rank"] = perkData->currentRank;
                    p["source"] = "runtime";
                    perks.push_back(p);
                } catch (...) {
                    continue;
                }
            }
        } catch (...) {
            // Fallback: try base TESNPC perk list if runtime data access fails
            SKSE::log::warn("Failed to access runtime perks, falling back to base perks");
        }

        // Also get base perks from TESNPC (starting perks from race/class)
        auto* base = player->GetActorBase();
        if (base) {
            auto* perkArray = base->As<RE::BGSPerkRankArray>();
            if (perkArray && perkArray->perks) {
                for (std::uint32_t i = 0; i < perkArray->perkCount; i++) {
                    auto& perkData = perkArray->perks[i];
                    if (!perkData.perk) continue;
                    try {
                        // Check for duplicates (already in runtime list)
                        std::string formIdStr = std::format("{:08X}", perkData.perk->GetFormID());
                        bool duplicate = false;
                        for (auto& existing : perks) {
                            if (existing.value("formId", "") == formIdStr) {
                                duplicate = true;
                                break;
                            }
                        }
                        if (duplicate) continue;

                        json p;
                        p["formId"] = formIdStr;
                        p["name"] = perkData.perk->GetName();
                        p["rank"] = perkData.currentRank;
                        p["source"] = "base";
                        perks.push_back(p);
                    } catch (...) {
                        continue;
                    }
                }
            }
        }

        return {{"perks", perks}, {"count", perks.size()}};
    }

    json GetAppearance() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* base = player->GetActorBase();
        if (!base) return {{"error", "Actor base not available"}};

        json result;
        result["race"] = base->GetRace() ? base->GetRace()->GetName() : "unknown";
        result["raceFormId"] = base->GetRace() ? std::format("{:08X}", base->GetRace()->GetFormID()) : "";
        result["sex"] = base->GetSex() == RE::SEX::kMale ? "male" : "female";
        result["weight"] = base->GetWeight();
        result["height"] = base->GetHeight();

        // Face morphs (19 values)
        if (base->faceData) {
            json morphs;
            static const char* morphNames[] = {
                "noseLongShort", "noseUpDown", "jawUpDown", "jawNarrowWide", "jawForwardBack",
                "cheeksUpDown", "cheeksForwardBack", "eyesUpDown", "eyesInOut",
                "browsUpDown", "browsInOut", "browsForwardBack", "lipsUpDown", "lipsInOut",
                "chinNarrowWide", "chinUpDown", "chinUnderbiteOverbite", "eyesForwardBack", "unk"
            };
            for (int i = 0; i < RE::TESNPC::FaceData::Morphs::kTotal; i++) {
                morphs[morphNames[i]] = base->faceData->morphs[i];
            }
            result["faceMorphs"] = morphs;

            json parts;
            static const char* partNames[] = { "nose", "unknown", "eyes", "mouth" };
            for (int i = 0; i < RE::TESNPC::FaceData::Parts::kTotal; i++) {
                parts[partNames[i]] = base->faceData->parts[i];
            }
            result["facePresets"] = parts;
        }

        // Head parts
        json headParts = json::array();
        if (base->headParts && base->numHeadParts > 0) {
            for (int i = 0; i < base->numHeadParts; i++) {
                auto* part = base->headParts[i];
                if (!part) continue;
                json hp;
                hp["formId"] = std::format("{:08X}", part->GetFormID());
                hp["name"] = part->GetName() ? part->GetName() : "";
                hp["type"] = static_cast<int>(part->type.get());
                headParts.push_back(hp);
            }
        }
        result["headParts"] = headParts;

        // Carry weight
        auto* avo = player->AsActorValueOwner();
        if (avo) {
            result["carryWeight"] = avo->GetActorValue(RE::ActorValue::kCarryWeight);
            result["carryWeightBase"] = avo->GetBaseActorValue(RE::ActorValue::kCarryWeight);
        }

        return result;
    }

    json GetFavorites() {
        auto* favorites = RE::MagicFavorites::GetSingleton();
        if (!favorites) return {{"error", "Favorites not available"}};

        json spells = json::array();
        for (auto* form : favorites->spells) {
            if (!form) continue;
            try {
                json f;
                f["formId"] = std::format("{:08X}", form->GetFormID());
                f["name"] = form->GetName();
                f["type"] = form->GetFormType() == RE::FormType::Spell ? "spell" :
                            form->GetFormType() == RE::FormType::Shout ? "shout" : "other";
                spells.push_back(f);
            } catch (...) {
                continue;
            }
        }

        return {{"favorites", spells}, {"count", spells.size()}};
    }

    json GetCharacterBlueprint() {
        // Single call that exports everything needed to recreate a character
        json blueprint;
        blueprint["exportDate"] = "auto";
        blueprint["exportSource"] = "Skyrim MCP Plugin v1.0";

        // Player info
        blueprint["character"] = GetPlayerInfo();

        // Stats
        blueprint["skills"] = GetSkillLevels();

        // Perks
        blueprint["perks"] = GetPerks();

        // Known spells
        blueprint["spells"] = GetKnownSpells();

        // Known shouts
        blueprint["shouts"] = GetKnownShouts();

        // Equipped items
        blueprint["equipped"] = GetEquippedItems();

        // Full inventory
        blueprint["inventory"] = GetInventory();

        // Gold
        blueprint["gold"] = GetGoldCount();

        // Active effects
        blueprint["activeEffects"] = GetActiveEffects();

        // Appearance (face morphs, head parts, race, sex)
        blueprint["appearance"] = GetAppearance();

        // Favorites
        blueprint["favorites"] = GetFavorites();

        // Phase 6+ additions
        blueprint["resistances"] = GetMagicResistances();
        blueprint["diseaseStatus"] = GetDiseaseStatus();
        blueprint["powers"] = GetPowers();

        // Cross-module data
        blueprint["factions"] = NPCManager::GetFactions();
        blueprint["damageStats"] = CombatAnalysis::GetDamageStats();

        return blueprint;
    }

    json IsInCombat() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        return {{"inCombat", player->IsInCombat()}};
    }

    json GetMagicResistances() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* avo = player->AsActorValueOwner();
        if (!avo) return {{"error", "Actor value owner not available"}};

        json result;
        result["magicResist"] = avo->GetActorValue(RE::ActorValue::kResistMagic);
        result["fireResist"] = avo->GetActorValue(RE::ActorValue::kResistFire);
        result["frostResist"] = avo->GetActorValue(RE::ActorValue::kResistFrost);
        result["shockResist"] = avo->GetActorValue(RE::ActorValue::kResistShock);
        result["poisonResist"] = avo->GetActorValue(RE::ActorValue::kPoisonResist);
        result["diseaseResist"] = avo->GetActorValue(RE::ActorValue::kResistDisease);

        return result;
    }

    static std::string DeliveryToString(RE::MagicSystem::Delivery d) {
        switch (d) {
            case RE::MagicSystem::Delivery::kSelf: return "self";
            case RE::MagicSystem::Delivery::kAimed: return "aimed";
            case RE::MagicSystem::Delivery::kTargetActor: return "targetActor";
            case RE::MagicSystem::Delivery::kTargetLocation: return "targetLocation";
            default: return "other";
        }
    }

    static std::string CastingTypeToString(RE::MagicSystem::CastingType c) {
        switch (c) {
            case RE::MagicSystem::CastingType::kConstantEffect: return "constantEffect";
            case RE::MagicSystem::CastingType::kFireAndForget: return "fireAndForget";
            case RE::MagicSystem::CastingType::kConcentration: return "concentration";
            default: return "other";
        }
    }

    json GetSpellDetails(const std::string& formIdHex) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(formIdHex));
            if (!form) return {{"error", "Form not found: " + formIdHex}};

            auto* spell = form->As<RE::SpellItem>();
            if (!spell) return {{"error", "Not a spell: " + formIdHex}};

            auto* player = RE::PlayerCharacter::GetSingleton();

            json result;
            result["formId"] = formIdHex;
            result["name"] = spell->GetName();
            result["type"] = Helpers::SpellTypeToString(spell->GetSpellType());
            result["castingType"] = CastingTypeToString(spell->GetCastingType());
            result["delivery"] = DeliveryToString(spell->GetDelivery());

            // Cost
            if (player) {
                result["cost"] = spell->CalculateMagickaCost(player);
            }
            result["costOverride"] = spell->data.costOverride;

            // Casting perk
            if (spell->data.castingPerk) {
                result["castingPerk"] = spell->data.castingPerk->GetName();
                result["castingPerkFormId"] = std::format("{:08X}", spell->data.castingPerk->GetFormID());
            }

            // School from costliest effect
            auto* costliest = spell->GetCostliestEffectItem();
            if (costliest && costliest->baseEffect) {
                result["school"] = Helpers::ActorValueToSchool(costliest->baseEffect->GetMagickSkill());
            }

            // All effects
            json effects = json::array();
            for (auto* effect : spell->effects) {
                if (!effect || !effect->baseEffect) continue;
                json e;
                e["name"] = effect->baseEffect->GetName();
                e["formId"] = std::format("{:08X}", effect->baseEffect->GetFormID());
                e["magnitude"] = effect->effectItem.magnitude;
                e["duration"] = effect->effectItem.duration;
                e["area"] = effect->effectItem.area;
                e["school"] = Helpers::ActorValueToSchool(effect->baseEffect->GetMagickSkill());
                e["isHostile"] = effect->IsHostile();
                effects.push_back(e);
            }
            result["effects"] = effects;

            return result;
        } catch (...) {
            return {{"error", "Failed to get spell details: " + formIdHex}};
        }
    }

    json GetEnchantmentInfo(const std::string& formIdHex) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        try {
            auto formId = Helpers::ParseFormId(formIdHex);

            // Search player inventory for this item
            auto inv = player->GetInventory();
            for (auto& [form, data] : inv) {
                if (!form || form->GetFormID() != formId) continue;

                auto& entryData = data.second;
                if (!entryData) continue;

                json result;
                result["formId"] = formIdHex;
                result["name"] = form->GetName();

                const char* displayName = entryData->GetDisplayName();
                if (displayName && displayName[0]) {
                    result["displayName"] = displayName;
                }

                auto* enchantment = entryData->GetEnchantment();
                if (!enchantment) {
                    result["enchanted"] = false;
                    return result;
                }

                result["enchanted"] = true;
                result["enchantmentName"] = enchantment->GetName();
                result["enchantmentFormId"] = std::format("{:08X}", enchantment->GetFormID());

                auto charge = entryData->GetEnchantmentCharge();
                if (charge.has_value()) {
                    result["charge"] = charge.value();
                }

                json effects = json::array();
                for (auto* effect : enchantment->effects) {
                    if (!effect || !effect->baseEffect) continue;
                    json e;
                    e["name"] = effect->baseEffect->GetName();
                    e["magnitude"] = effect->effectItem.magnitude;
                    e["duration"] = effect->effectItem.duration;
                    e["area"] = effect->effectItem.area;
                    effects.push_back(e);
                }
                result["effects"] = effects;

                return result;
            }

            return {{"error", "Item not found in inventory: " + formIdHex}};
        } catch (...) {
            return {{"error", "Failed to get enchantment info: " + formIdHex}};
        }
    }

    json GetPowers() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        json powers = json::array();
        json lesserPowers = json::array();
        json abilities = json::array();

        class PowerCollector : public RE::Actor::ForEachSpellVisitor {
        public:
            json& powers;
            json& lesserPowers;
            json& abilities;
            PowerCollector(json& p, json& lp, json& a) : powers(p), lesserPowers(lp), abilities(a) {}

            RE::BSContainer::ForEachResult Visit(RE::SpellItem* spell) override {
                if (!spell) return RE::BSContainer::ForEachResult::kContinue;
                try {
                    auto type = spell->GetSpellType();
                    json s;
                    s["formId"] = std::format("{:08X}", spell->GetFormID());
                    s["name"] = spell->GetName();

                    auto* costliest = spell->GetCostliestEffectItem();
                    if (costliest && costliest->baseEffect) {
                        s["school"] = Helpers::ActorValueToSchool(costliest->baseEffect->GetMagickSkill());
                        s["effectName"] = costliest->baseEffect->GetName();
                    }

                    if (type == RE::MagicSystem::SpellType::kPower) {
                        s["type"] = "power";
                        powers.push_back(s);
                    } else if (type == RE::MagicSystem::SpellType::kLesserPower) {
                        s["type"] = "lesserPower";
                        lesserPowers.push_back(s);
                    } else if (type == RE::MagicSystem::SpellType::kAbility) {
                        s["type"] = "ability";
                        abilities.push_back(s);
                    }
                } catch (...) {}
                return RE::BSContainer::ForEachResult::kContinue;
            }
        };

        PowerCollector collector(powers, lesserPowers, abilities);
        player->VisitSpells(collector);

        json result;
        result["powers"] = powers;
        result["lesserPowers"] = lesserPowers;
        result["abilities"] = abilities;
        result["powerCount"] = powers.size();
        result["lesserPowerCount"] = lesserPowers.size();
        result["abilityCount"] = abilities.size();

        return result;
    }

    json GetDiseaseStatus() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        // Check race for vampire/werewolf
        auto* base = player->GetActorBase();
        auto* race = base ? base->GetRace() : nullptr;
        std::string raceName = race ? race->GetName() : "";
        std::string raceEditorId = race ? (race->GetFormEditorID() ? race->GetFormEditorID() : "") : "";

        bool isVampire = false;
        bool isWerewolf = false;

        // Check by race editor ID
        std::string lowerRace = raceEditorId;
        std::transform(lowerRace.begin(), lowerRace.end(), lowerRace.begin(), ::tolower);
        if (lowerRace.find("vampire") != std::string::npos) isVampire = true;
        if (lowerRace.find("werewolf") != std::string::npos) isWerewolf = true;

        // Also check vampire/werewolf perk counts as indicator
        auto* avo = player->AsActorValueOwner();
        if (avo) {
            if (avo->GetActorValue(RE::ActorValue::kVampirePerks) > 0) isVampire = true;
            if (avo->GetActorValue(RE::ActorValue::kWerewolfPerks) > 0) isWerewolf = true;
        }

        // Scan active effects for diseases
        auto* magicTarget = player->AsMagicTarget();
        json diseases = json::array();

        if (magicTarget) {
            auto* effectList = magicTarget->GetActiveEffectList();
            if (effectList) {
                for (auto& effect : *effectList) {
                    if (!effect || !effect->GetBaseObject()) continue;
                    auto* baseEffect = effect->GetBaseObject();

                    // Check if the effect's spell is a disease type
                    auto* spell = effect->spell;
                    if (!spell) continue;

                    bool isDisease = false;
                    auto spellType = spell->GetSpellType();
                    if (spellType == RE::MagicSystem::SpellType::kDisease) isDisease = true;

                    // Also check by keyword — some diseases aren't typed as kDisease
                    if (baseEffect->HasKeywordString("MagicDamageDiseased")) isDisease = true;

                    if (isDisease) {
                        json d;
                        d["name"] = spell->GetName();
                        d["formId"] = std::format("{:08X}", spell->GetFormID());
                        d["effectName"] = baseEffect->GetName();
                        d["magnitude"] = effect->magnitude;
                        d["duration"] = effect->duration;
                        diseases.push_back(d);
                    }
                }
            }
        }

        json result;
        result["isVampire"] = isVampire;
        result["isWerewolf"] = isWerewolf;
        result["diseases"] = diseases;
        result["diseaseCount"] = diseases.size();
        result["race"] = raceName;

        return result;
    }

    json SetLevel(int level) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* base = player->GetActorBase();
        if (!base) return {{"error", "Player base not available"}};

        auto oldLevel = base->GetLevel();
        base->actorData.level = static_cast<std::uint16_t>(level);

        json result;
        result["set"] = true;
        result["oldLevel"] = oldLevel;
        result["newLevel"] = level;
        return result;
    }

    json ToggleGodMode() {
        REL::Relocation<bool*> godMode{ RELOCATION_ID(517711, 404238) };
        *godMode = !*godMode;
        return {{"godMode", *godMode}};
    }

    json ToggleImmortalMode() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        // Toggle essential flag directly on the actor runtime
        auto& flags = player->GetActorRuntimeData().boolFlags;
        bool wasEssential = flags.all(RE::Actor::BOOL_FLAGS::kEssential);

        if (wasEssential) {
            flags.reset(RE::Actor::BOOL_FLAGS::kEssential);
        } else {
            flags.set(RE::Actor::BOOL_FLAGS::kEssential);
        }

        json result;
        result["success"] = true;
        result["immortalMode"] = !wasEssential;  // Report intended state
        result["method"] = "native_essential_flag";
        return result;
    }

}

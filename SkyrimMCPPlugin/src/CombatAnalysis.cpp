#include "CombatAnalysis.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <format>

namespace SkyrimMCP::CombatAnalysis {

    using json = nlohmann::json;

    json GetCombatState() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        json result;
        result["inCombat"] = player->IsInCombat();

        auto* group = player->GetCombatGroup();
        if (group) {
            json targets = json::array();
            for (auto& target : group->targets) {
                auto targetPtr = target.targetHandle.get();
                if (!targetPtr) continue;
                auto* actor = targetPtr.get();
                if (!actor) continue;
                try {
                    json t;
                    t["refId"] = std::format("{:08X}", actor->GetFormID());
                    t["name"] = actor->GetName();
                    t["level"] = actor->GetLevel();
                    t["isDead"] = actor->IsDead();
                    auto* avo = actor->AsActorValueOwner();
                    if (avo) {
                        t["health"] = avo->GetActorValue(RE::ActorValue::kHealth);
                        t["healthMax"] = Helpers::ActorValueMax(actor, RE::ActorValue::kHealth);
                    }
                    targets.push_back(t);
                } catch (...) { continue; }
            }
            result["targets"] = targets;
            result["targetCount"] = targets.size();

            json allies = json::array();
            for (auto& member : group->members) {
                auto memberPtr = member.memberHandle.get();
                if (!memberPtr) continue;
                auto* actor = memberPtr.get();
                if (!actor || actor == player) continue;
                try {
                    json a;
                    a["refId"] = std::format("{:08X}", actor->GetFormID());
                    a["name"] = actor->GetName();
                    allies.push_back(a);
                } catch (...) { continue; }
            }
            result["allies"] = allies;
        }

        return result;
    }

    json GetDamageStats() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* avo = player->AsActorValueOwner();
        if (!avo) return {{"error", "Actor value owner not available"}};

        json result;
        result["damageResist"] = avo->GetActorValue(RE::ActorValue::kDamageResist);
        result["armorPerks"] = avo->GetActorValue(RE::ActorValue::kArmorPerks);
        result["magicResist"] = avo->GetActorValue(RE::ActorValue::kResistMagic);
        result["fireResist"] = avo->GetActorValue(RE::ActorValue::kResistFire);
        result["frostResist"] = avo->GetActorValue(RE::ActorValue::kResistFrost);
        result["shockResist"] = avo->GetActorValue(RE::ActorValue::kResistShock);
        result["poisonResist"] = avo->GetActorValue(RE::ActorValue::kPoisonResist);

        auto* rightHand = player->GetEquippedObject(false);
        if (rightHand) {
            auto* weapon = rightHand->As<RE::TESObjectWEAP>();
            if (weapon) {
                result["rightWeapon"] = weapon->GetName();
                result["rightWeaponDamage"] = weapon->GetAttackDamage();
            }
        }

        auto* leftHand = player->GetEquippedObject(true);
        if (leftHand) {
            auto* weapon = leftHand->As<RE::TESObjectWEAP>();
            if (weapon) {
                result["leftWeapon"] = weapon->GetName();
                result["leftWeaponDamage"] = weapon->GetAttackDamage();
            }
        }

        return result;
    }

    json GetThreats(float radius) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* processLists = RE::ProcessLists::GetSingleton();
        if (!processLists) return {{"error", "Process lists not available"}};

        auto playerPos = player->GetPosition();
        json threats = json::array();

        for (auto& actorHandle : processLists->highActorHandles) {
            auto actorPtr = actorHandle.get();
            if (!actorPtr) continue;

            auto* actor = actorPtr.get();
            if (!actor || actor == player || actor->IsDead()) continue;
            if (!actor->IsHostileToActor(player)) continue;

            auto actorPos = actor->GetPosition();
            float dx = actorPos.x - playerPos.x;
            float dy = actorPos.y - playerPos.y;
            float dz = actorPos.z - playerPos.z;
            float distance = std::sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > radius) continue;

            try {
                auto* base = actor->GetActorBase();
                auto* avo = actor->AsActorValueOwner();
                json t;
                t["refId"] = std::format("{:08X}", actor->GetFormID());
                t["name"] = actor->GetName();
                t["race"] = base && base->GetRace() ? base->GetRace()->GetName() : "unknown";
                t["level"] = actor->GetLevel();
                t["distance"] = distance;
                t["isEssential"] = actor->IsEssential();
                t["isInCombat"] = actor->IsInCombat();

                if (avo) {
                    t["health"] = avo->GetActorValue(RE::ActorValue::kHealth);
                    t["healthMax"] = Helpers::ActorValueMax(actor, RE::ActorValue::kHealth);
                }

                auto* equippedRight = actor->GetEquippedObject(false);
                if (equippedRight) {
                    t["weapon"] = equippedRight->GetName();
                    auto* weap = equippedRight->As<RE::TESObjectWEAP>();
                    if (weap) t["weaponDamage"] = weap->GetAttackDamage();
                }

                threats.push_back(t);
            } catch (...) { continue; }
        }

        return {{"threats", threats}, {"count", threats.size()}};
    }

}

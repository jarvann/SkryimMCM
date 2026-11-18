#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace PlayerQueries {
        json GetPlayerInfo();
        json GetInventory(const std::string& refId = "");
        json GetGoldCount();
        json GetActiveEffects();
        json GetEquippedItems();
        json GetKnownSpells();
        json GetKnownShouts();
        json GetSkillLevels();
        json GetPerks();
        json GetAppearance(const std::string& refId = "");
        json GetFavorites();
        json GetCharacterBlueprint();
        json IsInCombat();
        json GetMagicResistances();
        json GetDiseaseStatus();
        json GetSpellDetails(const std::string& formIdHex);
        json GetEnchantmentInfo(const std::string& formIdHex);
        json GetKnownEnchantments();
        json GetPowers();
        json SetLevel(int level);
        json ToggleGodMode();
        json ToggleImmortalMode();
    }

}

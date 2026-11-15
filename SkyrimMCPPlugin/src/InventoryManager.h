#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace InventoryManager {
        json AddItem(const std::string& formIdHex, int count);
        json RemoveItem(const std::string& formIdHex, int count);
        json EquipItem(const std::string& formIdHex);
        json UnequipItem(const std::string& formIdHex);
        json EquipItems(const json& items, const std::string& refId, bool unequip);
    }

}

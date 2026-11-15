#pragma once

#include <nlohmann/json.hpp>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Bulk item moves by category (FEAT-218).
    namespace LootManager {
        // {"from": refId|"player"|"crosshair", "to": ..., "categories": [...], "minValue", "maxCount",
        //  "includeEquipped", "includeFavorites", "allowStealing", "dryRun"}
        json TransferItems(const json& params);

        // {"radius", "categories", "minValue", "includeGold", "dryRun"}: take matching items from
        // dead actors near the player.
        json LootBodies(const json& params);
    }

}

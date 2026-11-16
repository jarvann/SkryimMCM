#pragma once

#include <nlohmann/json.hpp>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Character blueprint helpers: portable form IDs (FEAT-206) and applying the inventory,
    // equipment and gold sections (FEAT-207, the first slice of FEAT-121).
    namespace BlueprintManager {
        // Walks a blueprint and, next to every "formId", "baseId" or "...FormId" value, adds
        // "<key>Source": {"plugin": "Name.esp", "localId": "00ABCD"} so it survives a load-order
        // change. Forms created in game (FF......) get {"created": true}.
        void AddPortableIds(json& node);

        // {"inventory": [...items...], "equipped": [...], "gold": n, "sections": ["inventory","equipment","gold"],
        //  "exactGold": false, "dryRun": false}. Items come from a blueprint (GetCharacterBlueprint).
        // Only adds what is missing; never removes items (gold too, unless exactGold).
        json ApplyBlueprintItems(const json& params);
    }

}

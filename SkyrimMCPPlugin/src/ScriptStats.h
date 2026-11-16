#pragma once

#include <nlohmann/json.hpp>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Papyrus VM diagnostics (FEAT-150): how busy the script engine is and which scripts are
    // running right now, to find the mod behind script lag.
    namespace ScriptStats {
        // A snapshot taken on the game thread. top = how many scripts/functions to list.
        json GetScriptStats(int top);
    }

}

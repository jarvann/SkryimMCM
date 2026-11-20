#pragma once

#include <nlohmann/json.hpp>
#include <string>
#include <cstdint>
#include <vector>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Papyrus VM diagnostics (FEAT-150): how busy the script engine is and which scripts are
    // running right now, to find the mod behind script lag.
    namespace ScriptStats {
        // A snapshot taken on the game thread. top = how many scripts/functions to list.
        json GetScriptStats(int top);
        // FEAT-280: the script objects whose stacks started at an entry point ("Script.Function", or "Script" for all of its
        // handlers), with each object's VM handle so a function can be called on it (UnregisterForUpdate).
        json FindStackObjects(const std::string& entry, int limit);
        // FEAT-280: drop the OnUpdate registrations of these objects (as UnregisterForUpdate does), without the script engine.
        json UnregisterUpdates(const std::vector<std::uint64_t>& handles, bool gameTime);
    }

}

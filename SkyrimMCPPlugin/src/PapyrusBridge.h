#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace PapyrusBridge {
        // Scan .psc source files and build a function catalog
        json ScanPapyrusSources();

        // Get cached catalog (scan on first call, cache for subsequent)
        json GetPapyrusCatalog();

        // Call a Papyrus function via the VM
        // className: the script class (e.g., "ConsoleUtil")
        // functionName: the function (e.g., "ExecuteCommand")
        // args: JSON array of arguments with type hints
        json CallPapyrusFunction(const std::string& className,
                                  const std::string& functionName,
                                  const json& args,
                                  const std::string& target = "", bool wait = true);

        // Get capabilities for a specific script class
        json GetScriptFunctions(const std::string& className);
    }

}

#include "PapyrusBridge.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <filesystem>
#include <fstream>
#include <mutex>
#include <regex>
#include <future>

namespace SkyrimMCP::PapyrusBridge {

    using json = nlohmann::json;

    // ==================== Catalog Cache ====================

    static std::mutex s_catalogMutex;
    static json s_catalog;
    static bool s_catalogLoaded = false;

    static std::string GetCatalogPath() {
        auto skyrimPath = std::filesystem::current_path();
        return (skyrimPath / "Data" / "SKSE" / "Plugins" / "SkyrimMCP_PapyrusCatalog.json").string();
    }

    // ==================== PSC Scanner ====================

    struct ParsedFunction {
        std::string name;
        std::string returnType;
        std::vector<std::pair<std::string, std::string>> params;  // type, name
        bool isGlobal = false;
    };

    static std::vector<ParsedFunction> ParseNativeFunctions(const std::string& content) {
        std::vector<ParsedFunction> functions;

        // Match: [ReturnType] function FuncName(params) [global] native
        std::regex funcRegex(
            R"(^\s*(?:(\w+)\s+)?function\s+(\w+)\s*\(([^)]*)\)\s*(.*?native.*?)$)",
            std::regex::icase);

        std::regex paramRegex(R"((\w+)\s+(\w+))");

        auto begin = std::sregex_iterator(content.begin(), content.end(), funcRegex);
        auto end = std::sregex_iterator();

        for (auto it = begin; it != end; ++it) {
            auto& match = *it;
            std::string modifiers = match[4].str();

            // Only native functions
            std::string lowerMod = modifiers;
            std::transform(lowerMod.begin(), lowerMod.end(), lowerMod.begin(), ::tolower);
            if (lowerMod.find("native") == std::string::npos) continue;

            ParsedFunction func;
            func.returnType = match[1].matched ? match[1].str() : "void";
            func.name = match[2].str();
            func.isGlobal = lowerMod.find("global") != std::string::npos;

            // Parse parameters
            std::string paramStr = match[3].str();
            auto pBegin = std::sregex_iterator(paramStr.begin(), paramStr.end(), paramRegex);
            auto pEnd = std::sregex_iterator();
            for (auto pit = pBegin; pit != pEnd; ++pit) {
                func.params.push_back({(*pit)[1].str(), (*pit)[2].str()});
            }

            functions.push_back(func);
        }

        return functions;
    }

    json ScanPapyrusSources() {
        auto skyrimPath = std::filesystem::current_path();
        auto sourceDir = skyrimPath / "Data" / "Scripts" / "Source";

        if (!std::filesystem::exists(sourceDir)) {
            return {{"error", "Scripts/Source directory not found"}};
        }

        json catalog;
        catalog["version"] = 1;
        catalog["source"] = "runtime_psc_scan";
        int totalScripts = 0;
        int totalFunctions = 0;

        json scripts = json::object();

        for (auto& entry : std::filesystem::directory_iterator(sourceDir)) {
            if (!entry.is_regular_file()) continue;
            if (entry.path().extension() != ".psc") continue;

            try {
                std::ifstream file(entry.path());
                if (!file.is_open()) continue;

                std::string content((std::istreambuf_iterator<char>(file)),
                                     std::istreambuf_iterator<char>());
                file.close();

                auto functions = ParseNativeFunctions(content);
                if (functions.empty()) continue;

                // Get script name from scriptName declaration or filename
                std::string scriptName = entry.path().stem().string();
                std::regex scriptNameRegex(R"(^\s*scriptName\s+(\w+))", std::regex::icase);
                std::smatch snMatch;
                if (std::regex_search(content, snMatch, scriptNameRegex)) {
                    scriptName = snMatch[1].str();
                }

                json scriptJson;
                scriptJson["sourceFile"] = entry.path().filename().string();
                scriptJson["functionCount"] = functions.size();

                json funcArray = json::array();
                for (auto& func : functions) {
                    json f;
                    f["name"] = func.name;
                    f["returnType"] = func.returnType;
                    f["isGlobal"] = func.isGlobal;

                    json params = json::array();
                    for (auto& [type, name] : func.params) {
                        params.push_back({{"type", type}, {"name", name}});
                    }
                    f["params"] = params;
                    funcArray.push_back(f);
                }
                scriptJson["functions"] = funcArray;

                scripts[scriptName] = scriptJson;
                totalScripts++;
                totalFunctions += functions.size();

            } catch (...) {
                continue;
            }
        }

        catalog["totalScripts"] = totalScripts;
        catalog["totalFunctions"] = totalFunctions;
        catalog["scripts"] = scripts;

        // Write to cache file
        try {
            std::ofstream outFile(GetCatalogPath());
            if (outFile.is_open()) {
                outFile << catalog.dump(2);
                outFile.close();
                SKSE::log::info("Papyrus catalog written: {} scripts, {} functions",
                    totalScripts, totalFunctions);
            }
        } catch (...) {
            SKSE::log::error("Failed to write Papyrus catalog file");
        }

        return catalog;
    }

    json GetPapyrusCatalog() {
        std::lock_guard lock(s_catalogMutex);

        if (s_catalogLoaded) {
            return s_catalog;
        }

        // Try loading cached catalog
        try {
            std::ifstream file(GetCatalogPath());
            if (file.is_open()) {
                s_catalog = json::parse(file);
                s_catalogLoaded = true;
                SKSE::log::info("Papyrus catalog loaded from cache: {} scripts",
                    s_catalog.value("totalScripts", 0));
                return s_catalog;
            }
        } catch (...) {}

        // No cache — scan now
        SKSE::log::info("No Papyrus catalog cache found, scanning...");
        s_catalog = ScanPapyrusSources();
        s_catalogLoaded = true;
        return s_catalog;
    }

    json GetScriptFunctions(const std::string& className) {
        auto catalog = GetPapyrusCatalog();
        auto& scripts = catalog["scripts"];

        if (scripts.contains(className)) {
            return scripts[className];
        }

        // Case-insensitive search
        std::string lowerName = className;
        std::transform(lowerName.begin(), lowerName.end(), lowerName.begin(), ::tolower);

        for (auto& [name, data] : scripts.items()) {
            std::string lowerScript = name;
            std::transform(lowerScript.begin(), lowerScript.end(), lowerScript.begin(), ::tolower);
            if (lowerScript == lowerName) {
                return data;
            }
        }

        return {{"error", "Script class not found: " + className}};
    }

    // ==================== VM Call Bridge ====================

    // Custom callback functor that stores the result and signals a promise
    class PapyrusCallback : public RE::BSScript::IStackCallbackFunctor {
    public:
        std::promise<json> promise;

        PapyrusCallback() = default;

        void operator()(RE::BSScript::Variable a_result) override {
            try {
                json result;
                auto rawType = a_result.GetType().GetRawType();

                if (rawType == RE::BSScript::TypeInfo::RawType::kNone) {
                    result = nullptr;
                } else if (rawType == RE::BSScript::TypeInfo::RawType::kString) {
                    result = std::string(a_result.GetString());
                } else if (rawType == RE::BSScript::TypeInfo::RawType::kInt) {
                    result = a_result.GetSInt();
                } else if (rawType == RE::BSScript::TypeInfo::RawType::kFloat) {
                    result = a_result.GetFloat();
                } else if (rawType == RE::BSScript::TypeInfo::RawType::kBool) {
                    result = a_result.GetBool();
                } else {
                    result = "complex_type";
                }

                promise.set_value({{"result", result}});
            } catch (...) {
                promise.set_value({{"error", "Failed to unpack result"}});
            }
        }

        void SetObject(const RE::BSTSmartPointer<RE::BSScript::Object>&) override {}
    };

    json CallPapyrusFunction(const std::string& className,
                              const std::string& functionName,
                              const json& args) {
        auto* vm = RE::SkyrimVM::GetSingleton();
        if (!vm || !vm->GetVMRuntimeData().impl) {
            return {{"error", "Papyrus VM not available"}};
        }

        SKSE::log::info("Calling Papyrus: {}.{}({} args)", className, functionName, args.size());

        // Build function arguments from JSON array
        // Uses a custom IFunctionArguments that packs Variables at runtime
        class DynamicArgs : public RE::BSScript::IFunctionArguments {
        public:
            std::vector<RE::BSScript::Variable> vars;

            bool operator()(RE::BSScrapArray<RE::BSScript::Variable>& a_dst) const override {
                a_dst.resize(vars.size());
                for (size_t i = 0; i < vars.size(); i++) {
                    a_dst[i] = vars[i];
                }
                return true;
            }
        };

        auto* dynArgs = new DynamicArgs();

        if (args.is_array()) {
            for (auto& arg : args) {
                RE::BSScript::Variable v;
                if (arg.is_string()) {
                    v.SetString(arg.get<std::string>());
                } else if (arg.is_number_integer()) {
                    v.Pack(arg.get<std::int32_t>());
                } else if (arg.is_number_float()) {
                    v.SetFloat(arg.get<float>());
                } else if (arg.is_boolean()) {
                    v.SetBool(arg.get<bool>());
                }
                // TODO: Form types would need FormID resolution
                dynArgs->vars.push_back(v);
            }
        }

        RE::BSScript::IFunctionArguments* funcArgs = dynArgs;

        // Create callback
        auto callback = RE::BSTSmartPointer<RE::BSScript::IStackCallbackFunctor>(new PapyrusCallback());
        auto* callbackPtr = static_cast<PapyrusCallback*>(callback.get());

        auto future = callbackPtr->promise.get_future();

        // Dispatch the call
        bool dispatched = vm->GetVMRuntimeData().impl->DispatchStaticCall(
            RE::BSFixedString(className),
            RE::BSFixedString(functionName),
            funcArgs,
            callback);

        if (!dispatched) {
            return {{"error", "Failed to dispatch Papyrus call: " + className + "." + functionName}};
        }

        // Wait for result with timeout
        auto status = future.wait_for(std::chrono::seconds(5));
        if (status == std::future_status::timeout) {
            return {{"error", "Papyrus call timed out: " + className + "." + functionName},
                    {"note", "Function may have completed but result was not received in time"}};
        }

        auto result = future.get();
        result["className"] = className;
        result["functionName"] = functionName;
        return result;
    }

}

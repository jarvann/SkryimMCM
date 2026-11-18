#include "PapyrusBridge.h"
#include "LogicHost.h"
#include "Helpers.h"
#include "TaskQueue.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <regex>
#include <cstring>

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

    static json FunctionsFromVM(const std::string& className);

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

        // Not in the catalog: the catalog only reads loose .psc sources, and many mods (RaceMenu's
        // NiOverride and CharGen among them) ship their scripts packed in a BSA. Ask the VM instead.
        return FunctionsFromVM(className);
    }

    // ==================== VM Call Bridge ====================

    // The callback object the VM calls when the function returns is created by the host
    // DLL (HostApi::papyrusCallbackCreate), not here: the VM may still hold it after this
    // DLL has been reloaded, for example when a call times out on our side.
    static void AppendToString(void* ctx, const char* data, std::size_t len) {
        static_cast<std::string*>(ctx)->append(data, len);
    }

    // ==================== Pre-dispatch validation ====================
    //
    // DispatchStaticCall does not fail cleanly when the function is missing or the
    // arguments don't match: a call to a function that the installed version of a
    // mod doesn't have (e.g. PO3_SKSEFunctions.GetKnownEnchantments) crashed the
    // game. So every call is checked against the VM's own type info first, and only
    // a call that names a real global function with matching argument types is sent.

    static std::string TypeName(const RE::BSScript::TypeInfo& a_type) {
        using RawType = RE::BSScript::TypeInfo::RawType;
        if (a_type.IsArray()) return "Array";
        if (a_type.IsObject()) {
            auto* info = a_type.GetTypeInfo();
            return info && info->GetName() ? info->GetName() : "Object";
        }
        switch (a_type.GetRawType()) {
        case RawType::kNone:   return "None";
        case RawType::kString: return "String";
        case RawType::kInt:    return "Int";
        case RawType::kFloat:  return "Float";
        case RawType::kBool:   return "Bool";
        default:               return "Unknown";
        }
    }

    static std::string Signature(const std::string& a_className, RE::BSScript::IFunction* a_func) {
        std::string sig = TypeName(a_func->GetReturnType()) + " " + a_className + "." +
                          a_func->GetName().c_str() + "(";
        for (std::uint32_t i = 0; i < a_func->GetParamCount(); i++) {
            RE::BSFixedString name;
            RE::BSScript::TypeInfo type;
            a_func->GetParam(i, name, type);
            if (i > 0) sig += ", ";
            sig += TypeName(type) + " " + name.c_str();
        }
        return sig + ")";
    }

    static bool NameEquals(const RE::BSFixedString& a_name, const std::string& a_wanted) {
        return a_name.c_str() && _stricmp(a_name.c_str(), a_wanted.c_str()) == 0;
    }

    static RE::BSScript::IFunction* FindGlobalFunction(RE::BSScript::ObjectTypeInfo* a_type, const std::string& a_fn) {
        auto* funcs = a_type->GetGlobalFuncIter();
        for (std::uint32_t i = 0; funcs && i < a_type->GetNumGlobalFuncs(); i++) {
            auto* func = funcs[i].func.get();
            if (func && NameEquals(func->GetName(), a_fn)) return func;
        }
        return nullptr;
    }

    static bool HasMemberFunction(RE::BSScript::ObjectTypeInfo* a_type, const std::string& a_fn) {
        // Walk the parent chain: member functions are inherited.
        for (auto* type = a_type; type; type = type->GetParent()) {
            auto* funcs = type->GetMemberFuncIter();
            for (std::uint32_t i = 0; funcs && i < type->GetNumMemberFuncs(); i++) {
                auto* func = funcs[i].func.get();
                if (func && NameEquals(func->GetName(), a_fn)) return true;
            }
        }
        return false;
    }

    static json FunctionToJson(RE::BSScript::IFunction* a_func, bool a_global) {
        json params = json::array();
        for (std::uint32_t i = 0; i < a_func->GetParamCount(); i++) {
            RE::BSFixedString name;
            RE::BSScript::TypeInfo type;
            a_func->GetParam(i, name, type);
            params.push_back({{"type", TypeName(type)}, {"name", name.c_str() ? name.c_str() : ""}});
        }
        return {{"name", a_func->GetName().c_str() ? a_func->GetName().c_str() : ""},
                {"returnType", TypeName(a_func->GetReturnType())},
                {"isGlobal", a_global},
                {"isNative", a_func->GetIsNative()},
                {"params", params}};
    }

    // Lists a class's functions from the VM's own type info, which covers scripts the catalog can't
    // see (packed in BSAs). Runs on the game thread. GetScriptObjectType loads the class if needed.
    static json FunctionsFromVM(const std::string& className) {
        auto* vm = RE::SkyrimVM::GetSingleton();
        auto* impl = vm ? vm->GetVMRuntimeData().impl.get() : nullptr;
        if (!impl) return {{"error", "Papyrus VM not available"}};

        RE::BSTSmartPointer<RE::BSScript::ObjectTypeInfo> typeInfo;
        if (!impl->GetScriptObjectType(RE::BSFixedString(className), typeInfo) || !typeInfo) {
            return {{"error", "Script class not found: " + className +
                              " (not in the loose-source catalog, and the Papyrus VM doesn't know it either)"}};
        }
        auto* type = typeInfo.get();

        json functions = json::array();
        auto* globals = type->GetGlobalFuncIter();
        for (std::uint32_t i = 0; globals && i < type->GetNumGlobalFuncs(); i++) {
            if (auto* func = globals[i].func.get()) functions.push_back(FunctionToJson(func, true));
        }
        auto* members = type->GetMemberFuncIter();
        for (std::uint32_t i = 0; members && i < type->GetNumMemberFuncs(); i++) {
            if (auto* func = members[i].func.get()) functions.push_back(FunctionToJson(func, false));
        }
        std::sort(functions.begin(), functions.end(), [](const json& a, const json& b) {
            return a["name"].get<std::string>() < b["name"].get<std::string>();
        });

        json result = {{"source", "vm"},
                       {"className", type->GetName() ? type->GetName() : className},
                       {"functionCount", functions.size()},
                       {"functions", functions},
                       {"note", "Read from the Papyrus VM (the script isn't in the loose-source catalog). Member "
                                "functions inherited from parent classes are not listed; see 'parent'."}};
        if (auto* parent = type->GetParent(); parent && parent->GetName()) result["parent"] = parent->GetName();
        return result;
    }

    // Converts one JSON argument to a Variable of the parameter's declared type.
    // Returns an error message, or an empty string on success.
    static std::string PackArgument(const json& a_arg, const RE::BSScript::TypeInfo& a_type,
                                    const std::string& a_paramName, RE::BSScript::Variable& a_out, json& a_diag) {
        auto mismatch = [&](const char* a_expected) {
            return "Argument '" + a_paramName + "' must be " + a_expected + ", got " + a_arg.dump();
        };

        if (a_type.IsArray()) {
            return "Argument '" + a_paramName + "' is " + TypeName(a_type) +
                   "; call_papyrus_function can't pass arrays";
        }
        if (a_type.IsObject()) {
            // Forms and references (FEAT-227): a FormID, "player", or null for None. Packed on
            // the game thread, where the VM binds script objects.
            if (a_arg.is_null()) { a_out.SetNone(); return {}; }
            if (!a_arg.is_string()) return mismatch("a FormID string, \"player\" or null");
            const auto text = a_arg.get<std::string>();
            RE::TESForm* form = nullptr;
            if (text == "player") {
                form = RE::PlayerCharacter::GetSingleton();
            } else {
                try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(text)); } catch (...) {}
            }
            if (!form) return "Argument '" + a_paramName + "': no form with ID " + text;
            const auto* info = a_type.GetTypeInfo();
            const std::string expected = info && info->GetName() ? info->GetName() : "";
            const bool wantsRef = expected == "ObjectReference" || expected == "Actor";
            if (wantsRef && !form->AsReference()) {
                return "Argument '" + a_paramName + "' must be a reference (" + expected + "), but " + text +
                       " is a base form. Pass the reference ID (player, or a refId from the NPC tools).";
            }
            if (expected == "Actor" && !form->As<RE::Actor>()) {
                return "Argument '" + a_paramName + "' must be an Actor, but " + text + " isn't one";
            }
            // The task owns its Variable: if the wait times out, the game thread may still run it later.
            // BUG-066: in game every call given a form came back with no value (StorageUtil.GetIntValue(player, key, 7)
            // returned nothing; with None it returned 7). Report how each form was bound, so the step that fails shows.
            auto holder = std::make_shared<RE::BSScript::Variable>();
            json diag;
            try {
                diag = TaskQueue::RunOnGameThread([holder, form, a_paramName]() -> json {
                    const auto typeId = static_cast<RE::VMTypeID>(form->GetFormType());
                    json d = {{"param", a_paramName}, {"form", std::format("{:08X}", form->GetFormID())}, {"typeId", typeId}};
                    auto* vm = RE::BSScript::Internal::VirtualMachine::GetSingleton();
                    if (!vm) { d["problem"] = "no VM"; return d; }
                    RE::BSTSmartPointer<RE::BSScript::ObjectTypeInfo> classPtr;
                    const bool typed = vm->GetScriptObjectType(typeId, classPtr) && classPtr;
                    d["class"] = typed && classPtr->GetName() ? classPtr->GetName() : "(none)";
                    auto* policy = vm->GetObjectHandlePolicy1();
                    const auto handle = policy ? policy->GetHandleForObject(typeId, form) : 0;
                    d["handle"] = std::format("{:016X}", handle);
                    if (policy) {
                        d["handleAvailable"] = policy->IsHandleObjectAvailable(handle);
                        d["handleIsType"] = policy->HandleIsType(typeId, handle);
                    }
                    RE::BSTSmartPointer<RE::BSScript::Object> bound;
                    d["alreadyBound"] = typed && vm->FindBoundObject(handle, classPtr->GetName(), bound) && bound;
                    RE::BSScript::PackHandle(holder.get(), form, typeId);
                    d["packedIsObject"] = holder->IsObject();
                    if (auto obj = (holder->GetObject)()) {  // parenthesised: windows.h defines GetObject
                        d["objectClass"] = obj->GetTypeInfo() && obj->GetTypeInfo()->GetName() ? obj->GetTypeInfo()->GetName() : "(none)";
                        d["objectHandle"] = std::format("{:016X}", obj->GetHandle());
                        d["objectValid"] = obj->IsValid();
                        d["objectConstructed"] = obj->IsConstructed();
                        d["objectInitialized"] = obj->IsInitialized();
                        d["resolves"] = obj->Resolve(typeId) == form;
                    }
                    return d;
                });
            } catch (const std::exception& e) {
                return "Argument '" + a_paramName + "': " + e.what();
            }
            a_diag = diag;
            a_out = *holder;
            return {};
        }
        if (a_type.IsString()) {
            if (!a_arg.is_string()) return mismatch("a string");
            a_out.SetString(a_arg.get<std::string>());
        } else if (a_type.IsInt()) {
            if (!a_arg.is_number_integer()) return mismatch("an integer");
            a_out.SetSInt(a_arg.get<std::int32_t>());
        } else if (a_type.IsFloat()) {
            if (!a_arg.is_number()) return mismatch("a number");
            a_out.SetFloat(a_arg.get<float>());
        } else if (a_type.IsBool()) {
            if (!a_arg.is_boolean()) return mismatch("true or false");
            a_out.SetBool(a_arg.get<bool>());
        } else {
            return "Argument '" + a_paramName + "' has an unsupported type (" + TypeName(a_type) + ")";
        }
        return {};
    }

    static RE::BSScript::IFunction* FindMemberFunction(RE::BSScript::ObjectTypeInfo* a_type, const std::string& a_fn) {
        for (auto* type = a_type; type; type = type->GetParent()) {
            auto* funcs = type->GetMemberFuncIter();
            for (std::uint32_t i = 0; funcs && i < type->GetNumMemberFuncs(); i++) {
                auto* func = funcs[i].func.get();
                if (func && NameEquals(func->GetName(), a_fn)) return func;
            }
        }
        return nullptr;
    }

    json CallPapyrusFunction(const std::string& className,
                              const std::string& functionName,
                              const json& args,
                              const std::string& target) {
        auto* vm = RE::SkyrimVM::GetSingleton();
        auto* impl = vm ? vm->GetVMRuntimeData().impl.get() : nullptr;
        if (!impl) {
            return {{"error", "Papyrus VM not available"}};
        }

        // BUG-048: with a target (a form ID or editor ID of a form the script is attached to, such as a quest), call a
        // member function on that form's script instead of a global one.
        RE::TESForm* targetForm = nullptr;
        if (!target.empty()) {
            try { targetForm = RE::TESForm::LookupByID(Helpers::ParseFormId(target)); } catch (...) { targetForm = nullptr; }
            if (!targetForm) targetForm = RE::TESForm::LookupByEditorID(target);
            if (!targetForm) return {{"error", "Target not found: " + target + " (a form ID like 0001A2B3, or an editor ID)"}};
        }

        if (!args.is_array()) {
            return {{"error", "args must be a JSON array, e.g. [\"text\", 5, true]"}};
        }

        // 1. The script class must exist.
        RE::BSTSmartPointer<RE::BSScript::ObjectTypeInfo> typeInfo;
        if (!impl->GetScriptObjectType(RE::BSFixedString(className), typeInfo) || !typeInfo) {
            return {{"error", "Script class not found: " + className +
                              ". Check the spelling, or that the mod providing it is installed."}};
        }

        // 2. It must declare a global function with that name (or, with a target, a member function).
        auto* func = targetForm ? FindMemberFunction(typeInfo.get(), functionName) : FindGlobalFunction(typeInfo.get(), functionName);
        if (!func && targetForm) {
            return {{"error", "Member function not found: " + className + "." + functionName + ". Use get_script_functions to list what it has."}};
        }
        if (!func) {
            if (HasMemberFunction(typeInfo.get(), functionName)) {
                return {{"error", functionName + " is a member function of " + className +
                                  ", not a global one. call_papyrus_function can only call global functions."}};
            }
            return {{"error", "Function not found: " + className + "." + functionName +
                              ". The installed version of this script has no global function with that name. "
                              "Use get_script_functions to list what it has."}};
        }

        // 3. The arguments must match the declared parameters.
        const auto signature = Signature(className, func);
        const auto paramCount = func->GetParamCount();
        if (args.size() != paramCount) {
            return {{"error", std::format("{} expects {} argument(s), got {}. Signature: {}",
                                          functionName, paramCount, args.size(), signature)}};
        }

        // Packs Variables at runtime; the VM copies them during DispatchStaticCall.
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

        auto dynArgs = std::make_unique<DynamicArgs>();
        json packed = json::array();
        for (std::uint32_t i = 0; i < paramCount; i++) {
            RE::BSFixedString paramName;
            RE::BSScript::TypeInfo paramType;
            func->GetParam(i, paramName, paramType);

            RE::BSScript::Variable v;
            json diag;
            auto err = PackArgument(args[i], paramType, paramName.c_str() ? paramName.c_str() : "?", v, diag);
            if (!diag.is_null()) packed.push_back(diag);
            if (!err.empty()) {
                return {{"error", err + ". Signature: " + signature}};
            }
            dynArgs->vars.push_back(v);
        }

        SKSE::log::info("Calling Papyrus: {}", signature);

        auto* host = LogicHost::Get();
        if (!host) {
            return {{"error", "Papyrus calls unavailable: the plugin is reloading"}};
        }
        auto* rawCallback = static_cast<RE::BSScript::IStackCallbackFunctor*>(host->papyrusCallbackCreate());
        if (!rawCallback) {
            return {{"error", "Could not create a Papyrus callback"}};
        }
        // Holding this reference keeps the callback alive while we wait on it.
        RE::BSTSmartPointer<RE::BSScript::IStackCallbackFunctor> callback(rawCallback);

        // Dispatch the call (from the pipe thread; see Protocol.cpp for why not TaskQueue)
        bool dispatched = false;
        if (targetForm) {
            auto* policy = impl->GetObjectHandlePolicy1();
            const auto handle = policy ? policy->GetHandleForObject(targetForm->GetFormType(), targetForm) : 0;
            if (!handle || handle == (policy ? policy->EmptyHandle() : 0)) {
                return {{"error", "The target has no script handle (is the script attached to it?): " + target}};
            }
            dispatched = impl->DispatchMethodCall2(handle, RE::BSFixedString(className), RE::BSFixedString(functionName), dynArgs.get(), callback);
        } else {
            dispatched = impl->DispatchStaticCall(
                RE::BSFixedString(className),
                RE::BSFixedString(functionName),
                dynArgs.get(),
                callback);
        }

        if (!dispatched) {
            return {{"error", "Failed to dispatch Papyrus call: " + signature}};
        }

        // Wait for result with timeout
        std::string resultText;
        if (host->papyrusCallbackWait(rawCallback, 5000, &AppendToString, &resultText) != 0) {
            return {{"error", "Papyrus call timed out: " + signature},
                    {"note", "Function may have completed but result was not received in time"}};
        }

        json result;
        try {
            result = json::parse(resultText);
        } catch (...) {
            result = {{"error", "Unreadable Papyrus result"}};
        }
        result["className"] = className;
        result["functionName"] = functionName;
        result["signature"] = signature;
        if (targetForm) result["target"] = std::format("{:08X}", targetForm->GetFormID());
        if (!packed.empty()) result["packed"] = packed;
        return result;
    }

}

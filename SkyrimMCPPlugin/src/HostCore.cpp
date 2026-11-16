// CommonLibSSE headers MUST come before Windows API
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include "HostCore.h"

#include "BuildInfo.h"
#include "CrashRecorder.h"
#include "EventSystem.h"
#include "HostApi.h"
#include "LogicLoader.h"
#include "SkseInterfaceProxy.h"

#include <nlohmann/json.hpp>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <memory>
#include <mutex>

namespace SkyrimMCP::HostCore {

    using json = nlohmann::json;

    namespace {
        // Both well under the MCP server's 10-second pipe timeout: the request that
        // triggered an automatic reload still has to run afterwards.
        constexpr std::uint32_t kAutoDrainMs = 2000;
        constexpr std::uint32_t kManualDrainMs = 7000;

        std::unique_ptr<LogicLoader> g_loader;
        std::atomic<std::uint32_t> g_pendingTasks{ 0 };
        SkyLinkHostApi g_hostApi{};

        // ---- services the host gives the logic DLL ----

        void HostLog(int level, const char* message, std::size_t len) {
            if (auto* logger = spdlog::default_logger_raw()) {
                logger->log(static_cast<spdlog::level::level_enum>(level), "{}", std::string_view(message, len));
            }
        }

        int HostAddTask(SkyLinkTaskFn fn, void* ctx) {
            // The SKSE task object and this lambda are host code. The logic DLL's code
            // runs only inside fn, and the count drops only after fn has returned.
            // Counted before queueing: the game thread may run the task before AddTask
            // even returns.
            ++g_pendingTasks;
            try {
                SKSE::GetTaskInterface()->AddTask([fn, ctx]() {
                    struct Done {
                        ~Done() { --g_pendingTasks; }
                    } done;
                    try {
                        fn(ctx);
                    } catch (...) {
                        SKSE::log::error("A game-thread task from the logic DLL threw");
                    }
                });
                return 0;
            } catch (...) {
                --g_pendingTasks;
                return 1;
            }
        }

        // The callback object the Papyrus VM holds for a call made by the logic DLL.
        // It lives in the host, which is never unloaded, so a call still running when
        // the logic DLL is swapped (or one that timed out) calls back into valid code.
        class HostPapyrusCallback final : public RE::BSScript::IStackCallbackFunctor {
        public:
            void operator()(RE::BSScript::Variable a_result) override {
                json out;
                try {
                    auto rawType = a_result.GetType().GetRawType();
                    if (rawType == RE::BSScript::TypeInfo::RawType::kNone) {
                        out["result"] = nullptr;
                    } else if (rawType == RE::BSScript::TypeInfo::RawType::kString) {
                        out["result"] = std::string(a_result.GetString());
                    } else if (rawType == RE::BSScript::TypeInfo::RawType::kInt) {
                        out["result"] = a_result.GetSInt();
                    } else if (rawType == RE::BSScript::TypeInfo::RawType::kFloat) {
                        out["result"] = a_result.GetFloat();
                    } else if (rawType == RE::BSScript::TypeInfo::RawType::kBool) {
                        out["result"] = a_result.GetBool();
                    } else {
                        out["result"] = "complex_type";
                    }
                } catch (...) {
                    out = json{ { "error", "Failed to unpack result" } };
                }
                CrashRecorder::Record(CrashRecorder::Kind::Papyrus, "Papyrus call returned");
                {
                    std::scoped_lock lock(_mutex);
                    _result = out.dump();
                    _done = true;
                }
                _cv.notify_all();
            }

            void SetObject(const RE::BSTSmartPointer<RE::BSScript::Object>&) override {}

            bool Wait(std::uint32_t timeoutMs, std::string& result) {
                std::unique_lock lock(_mutex);
                if (!_cv.wait_for(lock, std::chrono::milliseconds(timeoutMs), [this]() { return _done; })) return false;
                result = _result;
                return true;
            }

        private:
            std::mutex _mutex;
            std::condition_variable _cv;
            bool _done = false;
            std::string _result;
        };

        void* HostPapyrusCallbackCreate() {
            try {
                return static_cast<RE::BSScript::IStackCallbackFunctor*>(new HostPapyrusCallback());
            } catch (...) {
                return nullptr;
            }
        }

        int HostPapyrusCallbackWait(void* callback, std::uint32_t timeoutMs, SkyLinkWriteFn write, void* ctx) {
            if (!callback) return 1;
            auto* functor = static_cast<HostPapyrusCallback*>(static_cast<RE::BSScript::IStackCallbackFunctor*>(callback));
            try {
                std::string result;
                if (!functor->Wait(timeoutMs, result)) return 1;
                write(ctx, result.data(), result.size());
                return 0;
            } catch (...) {
                return 1;
            }
        }

        // path::string() throws for characters outside the ANSI code page.
        std::string Text(const std::filesystem::path& path) {
            auto u8 = path.u8string();
            return std::string(u8.begin(), u8.end());
        }

        std::filesystem::path HostDirectory() {
            HMODULE self = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&HostDirectory), &self);
            std::wstring buffer(MAX_PATH, L'\0');
            for (;;) {
                DWORD len = GetModuleFileNameW(self, buffer.data(), static_cast<DWORD>(buffer.size()));
                if (len == 0) return {};
                if (len < buffer.size()) {
                    buffer.resize(len);
                    break;
                }
                buffer.resize(buffer.size() * 2);
            }
            return std::filesystem::path(buffer).parent_path();
        }

        std::filesystem::path ShadowDirectory() {
            wchar_t temp[MAX_PATH + 1]{};
            DWORD len = GetTempPathW(MAX_PATH + 1, temp);
            std::filesystem::path base = len ? std::filesystem::path(temp) : HostDirectory();
            return base / L"SkyLinkAI";
        }

        // ---- responses ----

        std::string Respond(const std::string& id, bool success, const json& data, const std::string& error = "") {
            json response;
            response["id"] = id;
            response["success"] = success;
            if (success) {
                response["data"] = data;
            } else {
                response["error"] = error;
            }
            return response.dump() + "\n";
        }

        json LogicStatusJson() {
            json logic;
            if (!g_loader) {
                logic["loaded"] = false;
                logic["error"] = "Plugin not started yet";
                return logic;
            }
            auto status = g_loader->Status();
            logic["loaded"] = status.loaded;
            if (status.loaded) {
                logic["version"] = status.version;
                logic["built"] = status.built;
                logic["loadedAt"] = status.loadedAt;
            }
            logic["reloads"] = status.reloads;
            logic["autoReload"] = true;
            logic["source"] = status.source;
            if (!status.error.empty()) logic["error"] = status.error;
            return logic;
        }

        // Tracks the loading screen, so reload results and the log show whether a reload
        // happened during one. A host object: the game keeps a pointer to every sink.
        class LoadingScreenWatcher final : public RE::BSTEventSink<RE::MenuOpenCloseEvent> {
        public:
            static LoadingScreenWatcher& Get() {
                static LoadingScreenWatcher watcher;
                return watcher;
            }

            void Register() {
                if (auto* ui = RE::UI::GetSingleton()) {
                    ui->AddEventSink<RE::MenuOpenCloseEvent>(this);
                } else {
                    SKSE::log::warn("UI not available; loading screens will not be tracked");
                }
            }

            bool Active() const { return active_.load(); }

            RE::BSEventNotifyControl ProcessEvent(const RE::MenuOpenCloseEvent* event,
                RE::BSTEventSource<RE::MenuOpenCloseEvent>*) override {
                if (event && event->menuName == RE::LoadingMenu::MENU_NAME) {
                    active_ = event->opening;
                    SKSE::log::info("Loading screen {}", event->opening ? "started" : "ended");
                    CrashRecorder::Record(CrashRecorder::Kind::Note,
                        event->opening ? "Loading screen started" : "Loading screen ended");
                }
                return RE::BSEventNotifyControl::kContinue;
            }

        private:
            std::atomic<bool> active_{ false };
        };

        // Before any request that reports on or uses the logic DLL: load a newer build if
        // one has been deployed, so check_connection never shows a build that is about to
        // be replaced. On failure the current build keeps serving; the error is logged.
        void AutoReloadIfNewer(const std::string& action) {
            if (g_loader && g_loader->ShouldAutoReload()) {
                SKSE::log::info("A newer logic DLL is on disk; reloading before '{}'{}", action,
                    LoadingScreenWatcher::Get().Active() ? " (during a loading screen)" : "");
                auto result = g_loader->Reload(true, kAutoDrainMs);
                if (result.ok && !result.skipped) {
                    CrashRecorder::Recordf(CrashRecorder::Kind::Note, "Logic DLL auto-reloaded before '{}': built {}",
                        action, result.built);
                } else if (!result.ok) {
                    CrashRecorder::Recordf(CrashRecorder::Kind::Note, "Logic DLL auto-reload failed: {}", result.error);
                }
            }
        }

        // get_game_safety during a loading screen. The logic DLL's check runs on the game
        // thread, which is busy loading, so it would time out instead of answering.
        std::string LoadingScreenSafety(const std::string& id) {
            json data;
            data["safe"] = false;
            data["isLoading"] = true;
            data["warning"] = "Game is loading — commands will crash";
            data["note"] = "Answered by the plugin host: the game thread is busy with the loading screen, so the "
                           "other checks are skipped. Call again once loading has finished.";
            return Respond(id, true, data);
        }

        std::string Ping(const std::string& id) {
            // Answered on the pipe thread, so it still works when game-thread calls do not.
            AutoReloadIfNewer("ping");
            json info;
            info["pluginVersion"] = SKYLINK_VERSION_STRING;
            info["pluginBuilt"] = BuildInfo::GetBuildTime();
            info["gameVersion"] = REL::Module::get().version().string(".");
            info["loadingScreen"] = LoadingScreenWatcher::Get().Active();
            info["logic"] = LogicStatusJson();
            return Respond(id, true, info);
        }

        std::string PollEvents(const std::string& id, const json& params) {
            auto events = EventSystem::GetSingleton().DrainEvents();
            auto includeTypes = params.value("eventTypes", json::array());
            auto excludeTypes = params.value("excludeTypes", json::array());

            auto listed = [](const json& list, const std::string& type) {
                for (auto& t : list) {
                    if (t.is_string() && t.get<std::string>() == type) return true;
                }
                return false;
            };

            json arr = json::array();
            for (auto& e : events) {
                std::string eventType = e.value("event", "");
                if (!includeTypes.empty() && !listed(includeTypes, eventType)) continue;
                if (!excludeTypes.empty() && listed(excludeTypes, eventType)) continue;
                arr.push_back(e);
            }

            auto mutedSummary = EventSystem::GetSingleton().GetMutedSummary();
            json result;
            result["events"] = arr;
            result["count"] = arr.size();
            result["mutedTotal"] = mutedSummary.value("mutedTotal", 0);
            if (mutedSummary.contains("muted") && !mutedSummary["muted"].empty()) {
                result["muted"] = mutedSummary["muted"];
            }
            return Respond(id, true, result);
        }

        std::string Reload(const std::string& id) {
            if (!g_loader) return Respond(id, false, {}, "Plugin not started yet");
            const bool duringLoadingScreen = LoadingScreenWatcher::Get().Active();
            if (duringLoadingScreen) SKSE::log::info("Manual reload requested during a loading screen");
            auto result = g_loader->Reload(false, kManualDrainMs);
            if (!result.ok) {
                CrashRecorder::Recordf(CrashRecorder::Kind::Note, "Logic DLL reload failed: {}", result.error);
                return Respond(id, false, {}, result.error);
            }
            CrashRecorder::Recordf(CrashRecorder::Kind::Note, "Logic DLL reloaded: built {} (was {})", result.built,
                result.previousBuilt);
            json data;
            data["reloaded"] = true;
            data["duringLoadingScreen"] = duringLoadingScreen;
            data["previousBuilt"] = result.previousBuilt;
            data["built"] = result.built;
            data["reloads"] = result.reloads;
            data["elapsedMs"] = result.elapsedMs;
            return Respond(id, true, data);
        }
    }

    void Start() {
        if (g_loader) return;

        LoadingScreenWatcher::Get().Register();

        g_hostApi.abiVersion = SKYLINK_ABI_VERSION;
        g_hostApi.skseLoadInterface = SkseInterfaceProxy::Get();
        g_hostApi.log = &HostLog;
        g_hostApi.addTask = &HostAddTask;
        g_hostApi.papyrusCallbackCreate = &HostPapyrusCallbackCreate;
        g_hostApi.papyrusCallbackWait = &HostPapyrusCallbackWait;

        auto source = HostDirectory() / L"SkyLinkAI" / L"SkyLinkLogic.dll";
        g_loader = std::make_unique<LogicLoader>(
            source, ShadowDirectory(), &g_hostApi,
            []() { return g_pendingTasks.load(); },
            [](int level, const std::string& message) {
                spdlog::log(static_cast<spdlog::level::level_enum>(level), "{}", message);
            });

        if (g_loader->Load()) {
            CrashRecorder::Recordf(CrashRecorder::Kind::Note, "Logic DLL loaded: built {}", g_loader->Status().built);
        } else {
            CrashRecorder::Recordf(CrashRecorder::Kind::Note, "Logic DLL not loaded: {}", g_loader->Status().error);
            SKSE::log::error("SkyLink logic DLL not loaded. Only ping, poll_events and reload_plugin will work "
                "until it is. Expected at {}", Text(source));
        }
    }

    namespace {
        std::string Dispatch(const std::string& requestLine, const json& request, const std::string& id,
            const std::string& action) {
            try {
                if (action == "ping") return Ping(id);
                if (action == "poll_events") return PollEvents(id, request.value("params", json::object()));
                if (action == "reload_plugin") return Reload(id);
                if (action == "get_game_safety" && LoadingScreenWatcher::Get().Active()) return LoadingScreenSafety(id);

                if (!g_loader) return Respond(id, false, {}, "Plugin not started yet");

                AutoReloadIfNewer(action);

                std::string response;
                if (!g_loader->Handle(requestLine, response)) {
                    auto status = g_loader->Status();
                    return Respond(id, false, {},
                        "SkyLink's logic DLL is not loaded" + (status.error.empty() ? std::string() : ": " + status.error) +
                        ". Check that SKSE\\Plugins\\SkyLinkAI\\SkyLinkLogic.dll is installed, then call reload_plugin "
                        "or restart the game.");
                }
                if (response.empty()) return Respond(id, false, {}, "The logic DLL returned no response");
                if (response.back() != '\n') response.push_back('\n');
                return response;
            } catch (const std::exception& e) {
                SKSE::log::error("Error handling action '{}': {}", action, e.what());
                return Respond(id, false, {}, e.what());
            }
        }

        // ---- crash recorder (FEAT-219) ----

        // The MCP server sends these constantly; recording them would push everything else
        // out of the ring buffer.
        bool RecordsRequest(const std::string& action) {
            return action != "ping" && action != "poll_events";
        }

        void RecordRequestStart(const std::string& action, const json& request) {
            if (!CrashRecorder::Enabled()) return;
            try {
                std::string params;
                const auto it = request.find("params");
                const bool hasParams = it != request.end() && it->is_object() && !it->empty();
                if (hasParams) params = it->dump(-1, ' ', false, json::error_handler_t::replace);
                CrashRecorder::Recordf(CrashRecorder::Kind::Request, "> {} {}", action, params);

                if (action == "call_papyrus_function" && hasParams) {
                    std::size_t argCount = 0;
                    if (auto args = it->find("args"); args != it->end() && args->is_array()) argCount = args->size();
                    CrashRecorder::Recordf(CrashRecorder::Kind::Papyrus, "Calling {}.{} ({} args)",
                        it->value("className", std::string("?")), it->value("functionName", std::string("?")), argCount);
                }
            } catch (...) {
            }
        }

        // Responses are dumped from nlohmann::json, whose object keys are sorted, so
        // "success" is the last top-level key: read the tail rather than parse a large reply.
        void RecordRequestEnd(const std::string& action, const std::string& response, long long elapsedMs) {
            if (!CrashRecorder::Enabled()) return;
            try {
                constexpr std::string_view kSuccess = "\"success\":";
                const auto pos = response.rfind(kSuccess);
                const bool ok = pos != std::string::npos && response.compare(pos + kSuccess.size(), 4, "true") == 0;
                if (ok) {
                    CrashRecorder::Recordf(CrashRecorder::Kind::Request, "< {} ok ({} ms)", action, elapsedMs);
                    return;
                }
                std::string error;
                try {
                    error = json::parse(response).value("error", std::string());
                } catch (...) {
                }
                CrashRecorder::Recordf(CrashRecorder::Kind::Request, "< {} error ({} ms): {}", action, elapsedMs, error);
            } catch (...) {
            }
        }
    }

    std::string HandleRequest(const std::string& requestLine) {
        json request;
        try {
            request = json::parse(requestLine);
        } catch (const json::parse_error& e) {
            return Respond("", false, {}, std::string("JSON parse error: ") + e.what());
        }

        std::string id = request.value("id", "");
        std::string action = request.value("action", "");

        if (!RecordsRequest(action)) return Dispatch(requestLine, request, id, action);

        // Crash recorder: an entry before the request runs, so a call that crashes the game
        // is still on record, and one with the outcome once it returns.
        RecordRequestStart(action, request);
        const auto started = std::chrono::steady_clock::now();
        std::string response = Dispatch(requestLine, request, id, action);
        const auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started).count();
        RecordRequestEnd(action, response, static_cast<long long>(elapsedMs));
        return response;
    }

}

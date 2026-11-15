// Entry points of SkyLinkLogic.dll, the reloadable half of the plugin (see HostApi.h).
// The host DLL loads a temp copy of this DLL, calls these exports, and can unload it
// and load a newer build while the game runs.

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <spdlog/sinks/base_sink.h>

#include "BuildInfo.h"
#include "HostApi.h"
#include "LogicHost.h"
#include "Protocol.h"

#include <nlohmann/json.hpp>

#include <atomic>
#include <mutex>

namespace {
    std::atomic<const SkyLinkHostApi*> g_host{ nullptr };
}

const SkyLinkHostApi* SkyrimMCP::LogicHost::Get() {
    return g_host.load();
}

namespace {

    // Sends this DLL's log lines to the host, which owns the log file.
    class HostSink final : public spdlog::sinks::base_sink<std::mutex> {
    protected:
        void sink_it_(const spdlog::details::log_msg& msg) override {
            if (auto* host = g_host.load(); host && host->log) {
                host->log(static_cast<int>(msg.level), msg.payload.data(), msg.payload.size());
            }
        }
        void flush_() override {}
    };

    void Write(SkyLinkWriteFn write, void* ctx, const std::string& text) noexcept {
        try {
            write(ctx, text.data(), text.size());
        } catch (...) {
            // The host's write callback copies into a std::string; nothing useful to do
            // if that fails, and no exception may leave this DLL.
        }
    }

    std::string ErrorResponse(const std::string& message) {
        nlohmann::json response;
        response["id"] = "";
        response["success"] = false;
        response["error"] = message;
        return response.dump() + "\n";
    }
}

// Every export is noexcept: an exception must never unwind into the host's frames.

extern "C" __declspec(dllexport) std::uint32_t SkyLink_AbiVersion() noexcept {
    return SKYLINK_ABI_VERSION;
}

extern "C" __declspec(dllexport) int SkyLink_Init(const SkyLinkHostApi* host) noexcept {
    if (!host || host->abiVersion != SKYLINK_ABI_VERSION || !host->log || !host->addTask ||
        !host->papyrusCallbackCreate || !host->papyrusCallbackWait) {
        return 1;
    }

    try {
        g_host = host;

        auto logger = std::make_shared<spdlog::logger>("SkyLinkLogic", std::make_shared<HostSink>());
        logger->set_level(spdlog::level::info);
        spdlog::set_default_logger(std::move(logger));

        if (host->skseLoadInterface) {
            SKSE::InitInfo info{};
            info.log = false;  // the host owns the log file
            SKSE::Init(static_cast<const SKSE::LoadInterface*>(host->skseLoadInterface), info);
        }

        SKSE::log::info("SkyLinkLogic v{} (built {}) initialised", SKYLINK_VERSION_STRING,
            SkyrimMCP::BuildInfo::GetBuildTime());
        return 0;
    } catch (...) {
        g_host = nullptr;
        return 2;
    }
}

extern "C" __declspec(dllexport) void SkyLink_HandleRequest(const char* request, std::size_t len,
    SkyLinkWriteFn write, void* ctx) noexcept {
    try {
        Write(write, ctx, SkyrimMCP::Protocol::HandleRequest(std::string(request, len)));
    } catch (const std::exception& e) {
        Write(write, ctx, ErrorResponse(std::string("Unhandled error: ") + e.what()));
    } catch (...) {
        Write(write, ctx, ErrorResponse("Unhandled error in the logic DLL"));
    }
}

extern "C" __declspec(dllexport) void SkyLink_GetInfo(SkyLinkWriteFn write, void* ctx) noexcept {
    try {
        nlohmann::json info;
        info["version"] = SKYLINK_VERSION_STRING;
        info["built"] = SkyrimMCP::BuildInfo::GetBuildTime();
        Write(write, ctx, info.dump());
    } catch (...) {
        Write(write, ctx, "{}");
    }
}

extern "C" __declspec(dllexport) void SkyLink_Shutdown() noexcept {
    // The host calls this once no request is running and no game-thread task of ours
    // is queued. Papyrus callbacks are host objects (HostApi::papyrusCallbackCreate),
    // so nothing the game holds points into this DLL any more.
    try {
        SKSE::log::info("SkyLinkLogic v{} (built {}) unloading for reload", SKYLINK_VERSION_STRING,
            SkyrimMCP::BuildInfo::GetBuildTime());
    } catch (...) {}
    g_host = nullptr;  // TaskQueue refuses new work; log lines from static destructors go nowhere
}

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <spdlog/sinks/basic_file_sink.h>

#include <cstring>
#include <string_view>

#include "BuildInfo.h"
#include "CrashRecorder.h"
#include "EventSystem.h"
#include "HostCore.h"
#include "HostFaceRefresh.h"
#include "PipeServer.h"
#include "SkseInterfaceProxy.h"

namespace {
    std::unique_ptr<SkyrimMCP::PipeServer> g_pipeServer;

    // A save name from an SKSE message (pre-load, save): bounded by dataLen.
    std::string_view SaveName(const SKSE::MessagingInterface::Message* a_msg) {
        if (!a_msg->data || a_msg->dataLen == 0) return "(unnamed)";
        const auto* text = static_cast<const char*>(a_msg->data);
        return std::string_view(text, strnlen(text, a_msg->dataLen));
    }

    void RecordPlayerCell() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* cell = player ? player->GetParentCell() : nullptr;
        if (!cell) return;
        const char* name = cell->GetName();
        SkyrimMCP::CrashRecorder::Recordf(SkyrimMCP::CrashRecorder::Kind::Cell, "Player cell {} [{:08X}]",
            name && *name ? name : "(unnamed)", cell->GetFormID());
    }

    void MessageCallback(SKSE::MessagingInterface::Message* a_msg) {
        switch (a_msg->type) {
            case SKSE::MessagingInterface::kDataLoaded:
                SKSE::log::info("Game data loaded, starting systems...");
                SkyrimMCP::CrashRecorder::Record(SkyrimMCP::CrashRecorder::Kind::Note, "Game data loaded");
                SkyrimMCP::CrashRecorder::StartQuitWatcher();

                // Console output: no hook needed — commands open console menu briefly

                SkyrimMCP::EventSystem::GetSingleton().Register();
                SKSE::log::info("Event system registered");

                // Command handlers live in SkyLinkAI\SkyLinkLogic.dll, which can be
                // reloaded while the game runs. Load it before the pipe opens.
                try {
                    SkyrimMCP::HostCore::Start();
                } catch (const std::exception& e) {
                    SKSE::log::error("Could not start the logic DLL loader: {}", e.what());
                } catch (...) {
                    SKSE::log::error("Could not start the logic DLL loader");
                }

                g_pipeServer = std::make_unique<SkyrimMCP::PipeServer>();
                g_pipeServer->Start();
                SKSE::log::info("Pipe server started on \\\\.\\pipe\\SkyrimMCP");
                break;

            case SKSE::MessagingInterface::kPreLoadGame:
                SKSE::log::info("Game loading...");
                SkyrimMCP::CrashRecorder::Recordf(SkyrimMCP::CrashRecorder::Kind::Note, "Loading save {}", SaveName(a_msg));
                break;

            case SKSE::MessagingInterface::kPostLoadGame:
                SkyrimMCP::HostCore::NoteGameLoaded();
                SKSE::log::info("Game loaded");
                // data is the load result (a bool passed as the pointer value).
                SkyrimMCP::CrashRecorder::Record(SkyrimMCP::CrashRecorder::Kind::Note,
                    a_msg->data ? "Save loaded" : "Save load failed");
                RecordPlayerCell();
                if (a_msg->data) SkyrimMCP::HostFaceRefresh::Schedule("save loaded");
                break;

            case SKSE::MessagingInterface::kSaveGame:
                SkyrimMCP::CrashRecorder::Recordf(SkyrimMCP::CrashRecorder::Kind::Note, "Game saved: {}", SaveName(a_msg));
                break;

            case SKSE::MessagingInterface::kNewGame:
                SkyrimMCP::HostCore::NoteGameLoaded();
                SkyrimMCP::CrashRecorder::Record(SkyrimMCP::CrashRecorder::Kind::Note, "New game started");
                break;
        }
    }

    void InitializeLogging() {
        auto path = SKSE::log::log_directory();
        if (!path) {
            SKSE::stl::report_and_fail("Failed to find SKSE log directory");
        }
        *path /= "SkyrimMCPPlugin.log";
        auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path->string(), true);
        auto log = std::make_shared<spdlog::logger>("global log", std::move(sink));
        log->set_level(spdlog::level::info);
        log->flush_on(spdlog::level::info);
        spdlog::set_default_logger(std::move(log));
    }
}

SKSEPluginInfo(
    .Version = { SKYLINK_VERSION_MAJOR, SKYLINK_VERSION_MINOR, SKYLINK_VERSION_PATCH, 0 },
    .Name = "SkyrimMCPPlugin",
    .Author = "SkyrimMCP",
    .RuntimeCompatibility = SKSE::VersionIndependence::AddressLibrary
)

SKSEPluginLoad(const SKSE::LoadInterface* a_skse) {
    InitializeLogging();
    SKSE::log::info("SkyrimMCPPlugin v{} (built {}) loading...", SKYLINK_VERSION_STRING,
        SkyrimMCP::BuildInfo::GetBuildTime());

    SKSE::Init(a_skse);
    SkyrimMCP::SkseInterfaceProxy::Capture(a_skse);  // the logic DLL's SKSE::Init uses this later

    // Steam, GOG and downgraded installs all report here; first thing to check in a bug report.
    SKSE::log::info("Skyrim runtime {}", REL::Module::get().version().string("."));

    // Crash recorder (FEAT-219): a memory-mapped ring buffer of recent activity that
    // survives a crash. Never throws; on failure it logs a warning and stays off.
    SkyrimMCP::CrashRecorder::Init();

    // Console output capture: opens console menu briefly during command execution

    auto messaging = SKSE::GetMessagingInterface();
    if (!messaging->RegisterListener(MessageCallback)) {
        SKSE::log::error("Failed to register messaging listener");
        return false;
    }

    SKSE::log::info("SkyrimMCPPlugin loaded successfully");
    return true;
}

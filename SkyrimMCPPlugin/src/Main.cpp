#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <spdlog/sinks/basic_file_sink.h>

#include <cstring>
#include <string_view>

#include "BuildInfo.h"
#include "CrashRecorder.h"
#include "EventSystem.h"
#include "HostCore.h"
#include <chrono>
#include <atomic>
#include <thread>
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

    // BUG-091: a save written by the console save command (and so by SkyLink's SaveGame, CheckSave and CleanAndReload) loads
    // back with the character ID at 00000000, and every save after it carries 00000000 (tested 2026-11-19: the file name
    // doesn't matter, only how the save was written). SkyLink keeps the ID in its own co-save record and puts it back after a
    // load that lost it. Lives in the host: SKSE keeps these callbacks for the whole session.
    namespace CharacterIdKeeper {
        constexpr std::uint32_t kUid = 'SKLK';
        constexpr std::uint32_t kRecord = 'CHID';
        std::uint32_t g_fromCosave = 0;

        void OnSave(SKSE::SerializationInterface* a_intfc) {
            auto* saves = RE::BGSSaveLoadManager::GetSingleton();
            const std::uint32_t id = saves ? saves->currentCharacterID : 0;
            const std::uint32_t keep = id != 0 ? id : g_fromCosave;   // never overwrite a known ID with 0
            if (id == 0 && keep != 0 && saves) saves->currentCharacterID = keep;
            if (keep != 0 && a_intfc->OpenRecord(kRecord, 1)) a_intfc->WriteRecordData(keep);
        }

        void OnLoad(SKSE::SerializationInterface* a_intfc) {
            std::uint32_t type = 0, version = 0, length = 0;
            while (a_intfc->GetNextRecordInfo(type, version, length)) {
                if (type == kRecord && length == sizeof(std::uint32_t)) {
                    std::uint32_t id = 0;
                    if (a_intfc->ReadRecordData(id)) g_fromCosave = id;
                }
            }
        }

        void OnRevert(SKSE::SerializationInterface*);

        std::atomic<unsigned> g_generation{ 0 };

        void OnRevert(SKSE::SerializationInterface*) { g_fromCosave = 0; ++g_generation; }

        // Sets the ID if the game has none. Game thread only.
        void ApplyIfLost(std::uint32_t a_id, const char* a_when) {
            auto* saves = RE::BGSSaveLoadManager::GetSingleton();
            if (!saves || saves->currentCharacterID != 0 || a_id == 0) return;
            saves->currentCharacterID = a_id;
            SKSE::log::info("Character ID was 00000000 {}; restored {:08X} from SkyLink's co-save record", a_when, a_id);
            SkyrimMCP::CrashRecorder::Recordf(SkyrimMCP::CrashRecorder::Kind::Note, "Character ID restored: {:08X}", a_id);
        }

        // Retest 2026-11-19: at kPostLoadGame the game still held the previous ID and cleared it afterwards, so a check there
        // never fired. Check again on the game thread over the next 20 seconds (host thread: the host never unloads).
        void RestoreAfterLoad() {
            const std::uint32_t id = g_fromCosave;
            auto* saves = RE::BGSSaveLoadManager::GetSingleton();
            SKSE::log::info("Character ID after load: game {:08X}, SkyLink co-save record {:08X}", saves ? saves->currentCharacterID : 0, id);
            if (id == 0) return;
            ApplyIfLost(id, "at load");
            const unsigned generation = ++g_generation;
            std::thread([id, generation]() {
                for (const int delayMs : { 500, 1000, 1500, 3000, 6000, 8000 }) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(delayMs));
                    if (g_generation.load() != generation) return;   // another load since
                    if (auto* tasks = SKSE::GetTaskInterface())
                        tasks->AddTask([id, generation]() { if (g_generation.load() == generation) ApplyIfLost(id, "after the load"); });
                }
            }).detach();
        }
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
                CharacterIdKeeper::RestoreAfterLoad();
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

    if (auto* serialization = SKSE::GetSerializationInterface()) {
        serialization->SetUniqueID(CharacterIdKeeper::kUid);
        serialization->SetSaveCallback(CharacterIdKeeper::OnSave);
        serialization->SetLoadCallback(CharacterIdKeeper::OnLoad);
        serialization->SetRevertCallback(CharacterIdKeeper::OnRevert);
    }

    auto messaging = SKSE::GetMessagingInterface();
    if (!messaging->RegisterListener(MessageCallback)) {
        SKSE::log::error("Failed to register messaging listener");
        return false;
    }

    SKSE::log::info("SkyrimMCPPlugin loaded successfully");
    return true;
}

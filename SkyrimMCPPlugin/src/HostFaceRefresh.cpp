#include "HostFaceRefresh.h"
#include "HostCore.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <atomic>
#include <chrono>
#include <format>
#include <string>
#include <thread>

namespace SkyrimMCP::HostFaceRefresh {

    namespace {
        std::atomic<unsigned> s_generation{ 0 };
    }

    void Schedule(const char* a_reason) {
        const unsigned generation = ++s_generation;
        std::string reason = a_reason ? a_reason : "";
        // A short-lived detached thread (host only: the host DLL never unloads). It sends an ordinary request through
        // HostCore, the same path as a pipe request, so the logic DLL runs it on the game thread.
        std::thread([generation, reason]() {
            for (const int delayMs : { 3000, 6000 }) {
                std::this_thread::sleep_for(std::chrono::milliseconds(delayMs));
                if (s_generation.load() != generation) return;  // a newer trigger will do it
                const auto request = std::format(R"({{"id":"face-refresh-{}","action":"reapply_face_state","params":{{"reason":"{}"}}}})",
                                                 generation, reason);
                const auto reply = HostCore::HandleRequest(request);
                SKSE::log::info("Face state refresh ({}): {}", reason, reply.substr(0, 300));
            }
        }).detach();
    }
}

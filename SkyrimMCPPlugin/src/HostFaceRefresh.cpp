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
            // BUG-050: in game the brows were set at +3 s and +6 s after a load and still came back hair-coloured
            // later (RaceMenu's UpdateTints queues a 3D update that re-tints them once its scripts get to it, which
            // can take a while on a heavy load order), so keep going for a minute.
            for (const int delayMs : { 3000, 5000, 7000, 15000, 30000 }) {
                std::this_thread::sleep_for(std::chrono::milliseconds(delayMs));
                if (s_generation.load() != generation) return;  // a newer trigger will do it
                // A request during a loading screen waits out the 5 s game-thread timeout (seen in GetSkyLinkCost) and, with
                // requests taking turns (INFRA-023), holds up the AI's calls meanwhile. Skip this try; the later ones run.
                if (HostCore::LoadingScreenActive()) {
                    SKSE::log::info("Face state refresh ({}): skipped during a loading screen", reason);
                    continue;
                }
                const auto request = std::format(R"({{"id":"face-refresh-{}","action":"reapply_face_state","params":{{"reason":"{}"}}}})",
                                                 generation, reason);
                const auto reply = HostCore::HandleRequest(request);
                SKSE::log::info("Face state refresh ({}): {}", reason, reply.substr(0, 300));
            }
        }).detach();
    }
}

#pragma once

#include <functional>
#include <future>

#include <nlohmann/json.hpp>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Dispatches work to the game's main thread. The work goes through the host DLL
    // (HostApi::addTask) rather than straight to SKSE, so the host can tell when none
    // of this DLL's code is still queued and it is safe to unload it.
    class TaskQueue {
    public:
        // Run on the game thread later; does not wait.
        static void Post(std::function<void()> work);

        // Submit work to the game thread. Blocks the calling thread until complete.
        // Throws on timeout (default 5 seconds).
        static json RunOnGameThread(std::function<json()> work, int timeoutMs = 5000);
    };

}

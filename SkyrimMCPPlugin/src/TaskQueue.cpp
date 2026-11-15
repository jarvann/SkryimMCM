#include "TaskQueue.h"
#include "LogicHost.h"

#include <SKSE/SKSE.h>

#include <chrono>
#include <memory>
#include <stdexcept>

namespace SkyrimMCP {

    namespace {
        // Called by the host on the game thread. Owns and frees the work item, and lets
        // no exception escape into the host's code.
        void RunPosted(void* ctx) {
            std::unique_ptr<std::function<void()>> work(static_cast<std::function<void()>*>(ctx));
            try {
                (*work)();
            } catch (const std::exception& e) {
                SKSE::log::error("Game-thread task failed: {}", e.what());
            } catch (...) {
                SKSE::log::error("Game-thread task failed");
            }
        }
    }

    void TaskQueue::Post(std::function<void()> work) {
        auto* host = LogicHost::Get();
        if (!host) {
            throw std::runtime_error("Game thread unavailable: the plugin is reloading");
        }
        auto* item = new std::function<void()>(std::move(work));
        if (host->addTask(&RunPosted, item) != 0) {
            delete item;
            throw std::runtime_error("Could not queue work for the game thread");
        }
    }

    json TaskQueue::RunOnGameThread(std::function<json()> work, int timeoutMs) {
        auto promise = std::make_shared<std::promise<json>>();
        auto future = promise->get_future();

        Post([promise, work = std::move(work)]() {
            try {
                promise->set_value(work());
            } catch (...) {
                promise->set_exception(std::current_exception());
            }
        });

        auto status = future.wait_for(std::chrono::milliseconds(timeoutMs));
        if (status == std::future_status::timeout) {
            throw std::runtime_error("Game thread task timed out - game may be loading or paused");
        }

        return future.get();
    }

}

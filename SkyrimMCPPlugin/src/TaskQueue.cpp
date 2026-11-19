// CommonLibSSE headers MUST come before Windows API
#include <SKSE/SKSE.h>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include "TaskQueue.h"
#include "LogicHost.h"

#include <atomic>
#include <chrono>
#include <format>
#include <memory>
#include <stdexcept>
#include <string>

namespace SkyrimMCP {

    namespace {
        // FEAT-220: a game-thread job that touches a bad form or pointer comes back as an
        // error instead of a crash to desktop. Only hardware faults are caught; C++
        // exceptions pass through to the normal try/catch, and a stack overflow is left
        // to crash, since the game can't safely continue after one.
        //
        // The trade-off: with /EHsc, destructors between the fault and this guard don't
        // run, so anything the faulting call held (memory, a lock in the engine) stays
        // held. That is why the error tells the player to save to a new slot.
        struct Fault {
            DWORD code = 0;
            void* address = nullptr;
        };

        std::atomic<std::uint32_t> g_faults{0};

        int FaultFilter(EXCEPTION_POINTERS* info, Fault* out) {
            switch (info->ExceptionRecord->ExceptionCode) {
                case EXCEPTION_ACCESS_VIOLATION:
                case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:
                case EXCEPTION_DATATYPE_MISALIGNMENT:
                case EXCEPTION_ILLEGAL_INSTRUCTION:
                case EXCEPTION_INT_DIVIDE_BY_ZERO:
                case EXCEPTION_PRIV_INSTRUCTION:
                    out->code = info->ExceptionRecord->ExceptionCode;
                    out->address = info->ExceptionRecord->ExceptionAddress;
                    return EXCEPTION_EXECUTE_HANDLER;
                default:
                    return EXCEPTION_CONTINUE_SEARCH;
            }
        }

        void Invoke(std::function<void()>* work) { (*work)(); }

        // No objects with destructors in this frame: MSVC doesn't allow them next to __try.
        bool CallGuarded(std::function<void()>* work, Fault* fault) {
            __try {
                Invoke(work);
                return true;
            } __except (FaultFilter(GetExceptionInformation(), fault)) {
                return false;
            }
        }

        std::string Describe(const Fault& fault) {
            const char* what = fault.code == EXCEPTION_ACCESS_VIOLATION ? "access violation"
                             : fault.code == EXCEPTION_INT_DIVIDE_BY_ZERO ? "divide by zero"
                             : fault.code == EXCEPTION_ILLEGAL_INSTRUCTION ? "illegal instruction"
                             : "hardware exception";
            std::string where = std::format("{}", fault.address);
            HMODULE module = nullptr;
            if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    static_cast<LPCSTR>(fault.address), &module) && module) {
                char path[MAX_PATH] = {};
                if (GetModuleFileNameA(module, path, MAX_PATH)) {
                    std::string name = path;
                    if (auto slash = name.find_last_of("\\/"); slash != std::string::npos) name.erase(0, slash + 1);
                    where = std::format("{}+0x{:X}", name,
                        reinterpret_cast<std::uintptr_t>(fault.address) - reinterpret_cast<std::uintptr_t>(module));
                }
            }
            return std::format("{} ({:08X}) at {}", what, static_cast<std::uint32_t>(fault.code), where);
        }

        // Runs work on this (game) thread. Returns an empty string, or the fault.
        std::string RunGuarded(std::function<void()>* work) {
            Fault fault;
            if (CallGuarded(work, &fault)) return {};
            g_faults.fetch_add(1);
            auto text = Describe(fault);
            SKSE::log::critical("Game-thread task crashed and was stopped: {}. The game may be unstable.", text);
            return text;
        }

        // Called by the host on the game thread. Owns and frees the work item, and lets
        // no exception escape into the host's code.
        void RunPosted(void* ctx) {
            std::unique_ptr<std::function<void()>> work(static_cast<std::function<void()>*>(ctx));
            try {
                RunGuarded(work.get());
            } catch (const std::exception& e) {
                SKSE::log::error("Game-thread task failed: {}", e.what());
            } catch (...) {
                SKSE::log::error("Game-thread task failed");
            }
        }
    }

    std::uint32_t TaskQueue::FaultCount() { return g_faults.load(); }

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
                std::function<void()> body = [&]() { promise->set_value(work()); };
                auto fault = RunGuarded(&body);
                if (!fault.empty()) {
                    promise->set_exception(std::make_exception_ptr(std::runtime_error(
                        "The game crashed inside this request (" + fault + ") and SkyLink stopped it. "
                        "The game may now be unstable: save to a NEW slot, then restart Skyrim.")));
                }
            } catch (...) {
                try { promise->set_exception(std::current_exception()); } catch (...) {}
            }
        });

        auto status = future.wait_for(std::chrono::milliseconds(timeoutMs));
        if (status == std::future_status::timeout) {
            // BUG-087: on a fresh install the game stops when its window loses focus, and every call times out.
            throw std::runtime_error("Game thread task timed out: the game may be loading, paused, or its window in the "
                                     "background (a game without bAlwaysActive=1 in Skyrim.ini stops when unfocused). "
                                     "CheckConnection shows whether the game is running tasks.");
        }

        return future.get();
    }

}

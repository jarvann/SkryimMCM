// Reload test harness: drives LogicLoader against a real SkyLinkLogic.dll outside
// Skyrim, with a fake host (log to stdout, a fake game thread that can be paused).
// Only requests that never touch game memory are sent: an unknown action and
// game_thread_ping. Local testing only; never shipped.
//
//   SkyLinkReloadHarness.exe <path to SkyLinkLogic.dll>
//
// Exit code 0 when every check passes.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "LogicLoader.h"

#include <nlohmann/json.hpp>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <deque>
#include <filesystem>
#include <format>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using namespace SkyrimMCP;
using json = nlohmann::json;
namespace fs = std::filesystem;

namespace {

    // ---- fake host ----

    std::mutex g_logMutex;
    bool g_verbose = false;

    void Print(const std::string& text) {
        std::scoped_lock lock(g_logMutex);
        std::printf("%s\n", text.c_str());
        std::fflush(stdout);
    }

    void HostLog(int level, const char* message, std::size_t len) {
        if (g_verbose || level >= 3) Print(std::format("    [log {}] {}", level, std::string_view(message, len)));
    }

    struct FakeGameThread {
        std::mutex mutex;
        std::condition_variable cv;
        std::deque<std::pair<SkyLinkTaskFn, void*>> queue;
        std::atomic<std::uint32_t> pending{ 0 };
        bool paused = false;
        bool stop = false;
        std::thread thread;

        void Start() {
            thread = std::thread([this]() {
                std::unique_lock lock(mutex);
                for (;;) {
                    cv.wait(lock, [this]() { return stop || (!paused && !queue.empty()); });
                    if (stop) return;
                    auto [fn, ctx] = queue.front();
                    queue.pop_front();
                    lock.unlock();
                    fn(ctx);
                    --pending;
                    lock.lock();
                }
            });
        }
        int Add(SkyLinkTaskFn fn, void* ctx) {
            ++pending;
            {
                std::scoped_lock lock(mutex);
                queue.emplace_back(fn, ctx);
            }
            cv.notify_all();
            return 0;
        }
        void Pause(bool value) {
            {
                std::scoped_lock lock(mutex);
                paused = value;
            }
            cv.notify_all();
        }
        void Stop() {
            {
                std::scoped_lock lock(mutex);
                stop = true;
            }
            cv.notify_all();
            thread.join();
        }
    } g_game;

    int HostAddTask(SkyLinkTaskFn fn, void* ctx) { return g_game.Add(fn, ctx); }

    // No Papyrus VM outside Skyrim; the harness never sends call_papyrus_function.
    void* HostPapyrusCallbackCreate() { return nullptr; }
    int HostPapyrusCallbackWait(void*, std::uint32_t, SkyLinkWriteFn, void*) { return 1; }

    // ---- checks ----

    int g_failures = 0;
    int g_passes = 0;

    void Check(bool ok, const std::string& what) {
        if (ok) {
            ++g_passes;
            Print("  PASS  " + what);
        } else {
            ++g_failures;
            Print("  FAIL  " + what);
        }
    }

    std::atomic<int> g_requestId{ 0 };

    json Send(LogicLoader& loader, const std::string& action, const json& params = json::object()) {
        json request{ { "id", std::to_string(++g_requestId) }, { "action", action }, { "params", params } };
        std::string response;
        if (!loader.Handle(request.dump(), response)) return json{ { "notLoaded", true } };
        try {
            return json::parse(response);
        } catch (...) {
            return json{ { "unparseable", response } };
        }
    }

    bool IsUnknownAction(const json& r) {
        return r.value("success", true) == false && r.value("error", "").starts_with("Unknown action");
    }

    bool IsGameThreadPong(const json& r) {
        return r.value("success", false) && r.contains("data") && r["data"].contains("gameThreadMs");
    }

    void SetLastWrite(const fs::path& file, std::int64_t secondsFromNow) {
        HANDLE h = CreateFileW(file.c_str(), FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return;
        FILETIME ft{};
        GetSystemTimeAsFileTime(&ft);
        ULARGE_INTEGER t{};
        t.LowPart = ft.dwLowDateTime;
        t.HighPart = ft.dwHighDateTime;
        t.QuadPart += static_cast<std::uint64_t>(secondsFromNow * 10'000'000LL);
        ft.dwLowDateTime = t.LowPart;
        ft.dwHighDateTime = t.HighPart;
        SetFileTime(h, nullptr, nullptr, &ft);
        CloseHandle(h);
    }
}

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: SkyLinkReloadHarness <path to SkyLinkLogic.dll> [-v]\n");
        return 2;
    }
    g_verbose = argc > 2 && std::string(argv[2]) == "-v";

    const fs::path built = fs::absolute(argv[1]);
    wchar_t tempBuf[MAX_PATH + 1]{};
    GetTempPathW(MAX_PATH + 1, tempBuf);
    const fs::path work = fs::path(tempBuf) / L"SkyLinkReloadHarness";
    const fs::path source = work / L"source" / L"SkyLinkLogic.dll";
    const fs::path shadow = work / L"shadow";
    std::error_code ec;
    fs::remove_all(work / L"source", ec);
    fs::create_directories(source.parent_path());
    fs::copy_file(built, source, fs::copy_options::overwrite_existing);
    SetLastWrite(source, -60);

    g_game.Start();

    SkyLinkHostApi api{};
    api.abiVersion = SKYLINK_ABI_VERSION;
    api.skseLoadInterface = nullptr;
    api.log = &HostLog;
    api.addTask = &HostAddTask;
    api.papyrusCallbackCreate = &HostPapyrusCallbackCreate;
    api.papyrusCallbackWait = &HostPapyrusCallbackWait;

    LogicLoader loader(source, shadow, &api, []() { return g_game.pending.load(); },
        [](int level, const std::string& m) { HostLog(level, m.data(), m.size()); });

    Print("== Initial load");
    Check(loader.Load(), "Load() succeeds");
    auto status = loader.Status();
    Check(status.loaded && !status.version.empty() && !status.built.empty(),
        std::format("status reports version {} built {}", status.version, status.built));
    Check(IsUnknownAction(Send(loader, "no_such_action")), "unknown action answered by the logic DLL");
    Check(IsGameThreadPong(Send(loader, "game_thread_ping")), "game_thread_ping round-trips through the fake game thread");
    Check(!loader.ShouldAutoReload(), "no auto-reload when the file is unchanged");

    Print("== 50 forced reloads with requests in between");
    {
        bool allOk = true;
        for (int i = 0; i < 50; ++i) {
            auto r = loader.Reload(false, 7000);
            if (!r.ok) {
                allOk = false;
                Print("    reload " + std::to_string(i) + " failed: " + r.error);
                break;
            }
            if (!IsUnknownAction(Send(loader, "no_such_action")) || !IsGameThreadPong(Send(loader, "game_thread_ping"))) {
                allOk = false;
                Print("    request after reload " + std::to_string(i) + " failed");
                break;
            }
        }
        Check(allOk, "50 reload cycles, each followed by working requests");
        Check(loader.Status().reloads == 50, "reload counter is 50");
    }

    Print("== Reloads while 4 threads send requests");
    {
        std::atomic<bool> stop{ false };
        std::atomic<int> bad{ 0 };
        std::atomic<int> sent{ 0 };
        std::vector<std::thread> clients;
        for (int t = 0; t < 4; ++t) {
            clients.emplace_back([&, t]() {
                while (!stop) {
                    auto r = (t % 2) ? Send(loader, "game_thread_ping") : Send(loader, "no_such_action");
                    bool ok = (t % 2) ? IsGameThreadPong(r) : IsUnknownAction(r);
                    if (!ok) {
                        ++bad;
                        Print("    bad response: " + r.dump());
                    }
                    ++sent;
                }
            });
        }
        int reloadFailures = 0;
        for (int i = 0; i < 25; ++i) {
            if (!loader.Reload(false, 7000).ok) ++reloadFailures;
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        stop = true;
        for (auto& c : clients) c.join();
        Check(reloadFailures == 0, "25 reloads under concurrent load all succeed");
        Check(bad == 0, std::format("{} concurrent requests, none failed", sent.load()));
    }

    Print("== Pending game-thread task blocks the reload");
    {
        g_game.Pause(true);
        std::thread waiter([&]() { Send(loader, "game_thread_ping", { { "timeoutMs", 1000 } }); });
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        auto refused = loader.Reload(false, 500);
        waiter.join();
        Check(!refused.ok && refused.error.find("postponed") != std::string::npos,
            "reload refused while a task from the current build is queued: " + refused.error);
        Check(IsUnknownAction(Send(loader, "no_such_action")), "current build still answers after the refusal");
        g_game.Pause(false);
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        auto r = loader.Reload(false, 7000);
        Check(r.ok, "reload succeeds once the task has run");
    }

    Print("== Postponed auto-reload backs off");
    {
        g_game.Pause(true);
        std::thread waiter([&]() { Send(loader, "game_thread_ping", { { "timeoutMs", 300 } }); });
        waiter.join();  // timed out; its task is still queued
        SetLastWrite(source, -40);
        Check(loader.ShouldAutoReload(), "a new build is noticed");
        auto started = std::chrono::steady_clock::now();
        auto postponed = loader.Reload(true, 300);
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
        Check(!postponed.ok && postponed.error.find("postponed") != std::string::npos,
            "auto-reload postponed while a task is queued");
        Check(ms < 1500, std::format("postponing took {} ms (no build loaded while waiting)", ms));
        Check(!loader.ShouldAutoReload(), "not retried on the very next request");
        g_game.Pause(false);
        std::this_thread::sleep_for(std::chrono::milliseconds(5200));
        Check(loader.ShouldAutoReload(), "retried after the back-off");
        Check(loader.Reload(true, 2000).ok, "and then loads");
    }

    Print("== Auto-reload detection");
    {
        SetLastWrite(source, -30);  // "a new build was copied in 30 s ago"
        Check(loader.ShouldAutoReload(), "a changed, settled file is picked up");
        auto r = loader.Reload(true, 2000);
        Check(r.ok, "only-if-changed reload loads it");
        Check(!loader.ShouldAutoReload(), "and then reports nothing new");
        auto skipped = loader.Reload(true, 2000);
        Check(skipped.skipped, "only-if-changed reload of an unchanged file is skipped");

        SetLastWrite(source, 0);  // just written
        Check(!loader.ShouldAutoReload(), "a file written this second is left alone (copy may be in progress)");

        SetLastWrite(source, -30);
        HANDLE writer = CreateFileW(source.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        Check(writer != INVALID_HANDLE_VALUE, "harness opened the DLL for writing");
        Check(!loader.ShouldAutoReload(), "a file still open for writing is left alone");
        auto busy = loader.Reload(false, 2000);
        Check(!busy.ok && busy.error.find("being written") != std::string::npos,
            "an explicit reload of a file being written is refused: " + busy.error);
        Check(IsUnknownAction(Send(loader, "no_such_action")), "the current build keeps answering");
        if (writer != INVALID_HANDLE_VALUE) CloseHandle(writer);
        SetLastWrite(source, -30);
        std::this_thread::sleep_for(std::chrono::milliseconds(2200));
        Check(loader.ShouldAutoReload(), "picked up once the writer is done (not marked as a broken build)");
        Check(loader.Reload(true, 2000).ok, "and loads");
    }

    Print("== Broken build on disk");
    {
        loader.Reload(true, 2000);  // settle on the current file
        fs::copy_file(source, work / L"good.dll", fs::copy_options::overwrite_existing);
        {
            std::ofstream junk(source, std::ios::binary | std::ios::trunc);
            junk << "not a dll";
        }
        SetLastWrite(source, -30);
        Check(loader.ShouldAutoReload(), "the broken file looks new");
        auto r = loader.Reload(true, 2000);
        Check(!r.ok, "loading it fails: " + r.error);
        Check(IsUnknownAction(Send(loader, "no_such_action")), "the previous build keeps answering");
        Check(!loader.ShouldAutoReload(), "the failed build is not retried on every request");
        Check(!loader.Status().error.empty(), "status reports the failure");

        fs::copy_file(work / L"good.dll", source, fs::copy_options::overwrite_existing);
        SetLastWrite(source, -20);
        Check(loader.ShouldAutoReload(), "a fixed build is picked up");
        Check(loader.Reload(true, 2000).ok, "and loads");
        Check(loader.Status().error.empty(), "status error clears");
    }

    Print("== Missing file");
    {
        fs::rename(source, work / L"moved.dll");
        auto r = loader.Reload(false, 7000);
        Check(!r.ok, "reload with the DLL missing fails: " + r.error);
        Check(IsUnknownAction(Send(loader, "no_such_action")), "the loaded build keeps answering");
        fs::rename(work / L"moved.dll", source);
    }

    Print("== Temp copies");
    {
        int copies = 0;
        for (auto& e : fs::directory_iterator(shadow, ec)) {
            if (e.path().filename().wstring().starts_with(L"SkyLinkLogic-")) ++copies;
        }
        Check(copies == 1, std::format("one temp copy left in {} ({} found)", shadow.string(), copies));
    }

    g_game.Stop();
    Print(std::format("\n{} passed, {} failed", g_passes, g_failures));
    return g_failures == 0 ? 0 : 1;
}

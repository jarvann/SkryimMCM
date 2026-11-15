#pragma once

// Loads, runs and hot-swaps the logic DLL (see HostApi.h). Plain Win32 and the
// standard library only, no CommonLib, so the reload test harness can use it outside
// Skyrim.

#include "HostApi.h"

#include <cstdint>
#include <filesystem>
#include <functional>
#include <mutex>
#include <shared_mutex>
#include <string>

namespace SkyrimMCP {

    struct LogicStatus {
        bool loaded = false;
        std::string version;
        std::string built;
        std::string loadedAt;
        std::string source;
        std::string error;      // why the logic DLL is not loaded, or why the last reload failed
        std::uint32_t reloads = 0;
    };

    struct ReloadResult {
        bool ok = false;
        bool skipped = false;   // only-if-changed reload, and the file has not changed
        std::string error;
        std::string previousBuilt;
        std::string built;
        std::uint32_t reloads = 0;
        long long elapsedMs = 0;
    };

    class LogicLoader {
    public:
        using LogFn = std::function<void(int level, const std::string& message)>;  // spdlog levels
        using PendingTasksFn = std::function<std::uint32_t()>;

        LogicLoader(std::filesystem::path source, std::filesystem::path shadowDir,
            const SkyLinkHostApi* host, PendingTasksFn pendingTasks, LogFn log);

        // Deliberately does not unload: this only runs at process exit, when calling
        // into another DLL is unsafe.
        ~LogicLoader() = default;

        LogicLoader(const LogicLoader&) = delete;
        LogicLoader& operator=(const LogicLoader&) = delete;

        // First load. Deletes temp copies left by earlier game sessions.
        bool Load();

        // Runs one request through the logic DLL. Returns false, leaving `response`
        // untouched, when no logic DLL is loaded. Requests run concurrently with each
        // other; a reload waits for them to finish.
        bool Handle(const std::string& request, std::string& response);

        // Swaps in the DLL now on disk. Waits up to drainMs for game-thread tasks the
        // current build queued; if they have not run by then the reload is postponed
        // and the current build keeps serving. The new build is loaded and checked
        // before the current one is released, and the previous build is restored if
        // the new one fails to initialise. Keep drainMs well under the MCP server's
        // 10-second pipe timeout: the request that triggered the reload waits too.
        ReloadResult Reload(bool onlyIfChanged, std::uint32_t drainMs);

        // True when the DLL on disk differs from the loaded one, has finished being
        // written, is not a build that already failed to load, and is not inside the
        // back-off after a postponed attempt.
        bool ShouldAutoReload();

        LogicStatus Status();

    private:
        struct FileStamp {
            std::uint64_t lastWrite = 0;    // FILETIME as 100 ns ticks
            std::uint64_t size = 0;
            bool operator==(const FileStamp&) const = default;
        };

        struct Module {
            void* handle = nullptr;         // HMODULE
            std::filesystem::path shadow;
            FileStamp stamp;
            std::string version;
            std::string built;
            SkyLinkInitFn init = nullptr;
            SkyLinkHandleRequestFn handle_request = nullptr;
            SkyLinkGetInfoFn get_info = nullptr;
            SkyLinkShutdownFn shutdown = nullptr;
        };

        enum class LoadFailure { None, Transient, Permanent };

        bool StampOf(const std::filesystem::path& file, FileStamp& stamp) const;
        LoadFailure CopyToShadow(const std::filesystem::path& from, const std::filesystem::path& to,
            FileStamp& stamp, std::string& error);
        LoadFailure LoadModule(const std::filesystem::path& from, bool copyFirst, Module& out, std::string& error);
        void ReleaseModule(Module& module, bool deleteShadow);
        void Postpone(const FileStamp& stamp, int seconds);
        void Log(int level, const std::string& message) const;

        const std::filesystem::path _source;
        const std::filesystem::path _shadowDir;
        const SkyLinkHostApi* const _host;
        const PendingTasksFn _pendingTasks;
        const LogFn _log;

        std::shared_mutex _mutex;           // shared: requests; exclusive: reload
        Module _current;
        std::string _loadedAt;
        std::string _lastError;
        std::uint32_t _reloads = 0;
        std::uint32_t _sequence = 0;

        std::mutex _stampMutex;             // guards the fields below, read without _mutex
        FileStamp _loadedStamp;
        FileStamp _failedStamp;             // a build that failed to load: never retried automatically
        FileStamp _postponedStamp;          // a build whose reload was postponed: retried after a back-off
        std::uint64_t _retryAfterTicks = 0;
    };

}

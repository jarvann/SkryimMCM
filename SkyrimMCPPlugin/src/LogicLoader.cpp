#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "LogicLoader.h"

#include <nlohmann/json.hpp>

#include <chrono>
#include <ctime>
#include <format>
#include <thread>
#include <vector>

namespace SkyrimMCP {

    namespace {
        // spdlog level values, so this file does not need spdlog
        constexpr int kInfo = 2;
        constexpr int kWarn = 3;
        constexpr int kError = 4;

        constexpr const wchar_t* kShadowPrefix = L"SkyLinkLogic-";
        constexpr std::uint64_t kTicksPerSecond = 10'000'000ULL;

        void AppendToString(void* ctx, const char* data, std::size_t len) {
            static_cast<std::string*>(ctx)->append(data, len);
        }

        // path::string() throws for characters outside the ANSI code page (a user or
        // folder name in another script); UTF-8 never does.
        std::string Text(const std::filesystem::path& path) {
            auto u8 = path.u8string();
            return std::string(u8.begin(), u8.end());
        }

        std::string LocalTimeNow() {
            std::time_t now = std::time(nullptr);
            std::tm local{};
            if (localtime_s(&local, &now) != 0) return "unknown";
            char text[32];
            std::strftime(text, sizeof(text), "%Y-%m-%d %H:%M:%S", &local);
            return text;
        }

        std::string LastErrorText(DWORD code) {
            char* buffer = nullptr;
            DWORD len = FormatMessageA(
                FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                nullptr, code, 0, reinterpret_cast<LPSTR>(&buffer), 0, nullptr);
            std::string text = len ? std::string(buffer, len) : std::string();
            if (buffer) LocalFree(buffer);
            while (!text.empty() && (text.back() == '\n' || text.back() == '\r' || text.back() == '.')) text.pop_back();
            return std::format("{} (error {})", text.empty() ? "unknown error" : text, code);
        }

        std::uint64_t NowTicks() {
            FILETIME ft{};
            GetSystemTimeAsFileTime(&ft);
            return (static_cast<std::uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
        }

        std::uint64_t Ticks(const FILETIME& ft) {
            return (static_cast<std::uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
        }

        struct HandleCloser {
            HANDLE h;
            ~HandleCloser() { if (h != INVALID_HANDLE_VALUE) CloseHandle(h); }
        };
    }

    LogicLoader::LogicLoader(std::filesystem::path source, std::filesystem::path shadowDir,
        const SkyLinkHostApi* host, PendingTasksFn pendingTasks, LogFn log) :
        _source(std::move(source)),
        _shadowDir(std::move(shadowDir)),
        _host(host),
        _pendingTasks(std::move(pendingTasks)),
        _log(std::move(log)) {}

    void LogicLoader::Log(int level, const std::string& message) const {
        if (_log) _log(level, message);
    }

    bool LogicLoader::StampOf(const std::filesystem::path& file, FileStamp& stamp) const {
        WIN32_FILE_ATTRIBUTE_DATA data{};
        if (!GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &data)) return false;
        stamp.lastWrite = Ticks(data.ftLastWriteTime);
        stamp.size = (static_cast<std::uint64_t>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
        return true;
    }

    LogicLoader::LoadFailure LogicLoader::CopyToShadow(const std::filesystem::path& from,
        const std::filesystem::path& to, FileStamp& stamp, std::string& error) {
        // Open the build with read sharing only: if a deploy still has it open for
        // writing this fails, and while we hold it no deploy can start writing. The
        // stamp and the bytes then come from the same, complete file.
        HandleCloser src{ CreateFileW(from.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr) };
        if (src.h == INVALID_HANDLE_VALUE) {
            DWORD code = GetLastError();
            if (code == ERROR_SHARING_VIOLATION || code == ERROR_LOCK_VIOLATION) {
                error = "The logic DLL is still being written (deploy in progress)";
                return LoadFailure::Transient;
            }
            error = std::format("Could not open {}: {}", Text(from), LastErrorText(code));
            return code == ERROR_FILE_NOT_FOUND || code == ERROR_PATH_NOT_FOUND ? LoadFailure::Transient
                                                                                : LoadFailure::Permanent;
        }

        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(src.h, &info)) {
            error = std::format("Could not read {}: {}", Text(from), LastErrorText(GetLastError()));
            return LoadFailure::Transient;
        }
        stamp.lastWrite = Ticks(info.ftLastWriteTime);
        stamp.size = (static_cast<std::uint64_t>(info.nFileSizeHigh) << 32) | info.nFileSizeLow;
        if (stamp.size == 0 || stamp.size > 256ULL * 1024 * 1024) {
            error = std::format("{} has an unexpected size ({} bytes)", Text(from), stamp.size);
            return LoadFailure::Permanent;
        }

        std::vector<char> bytes(static_cast<std::size_t>(stamp.size));
        std::size_t done = 0;
        while (done < bytes.size()) {
            DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(bytes.size() - done, 1 << 20));
            DWORD got = 0;
            if (!ReadFile(src.h, bytes.data() + done, chunk, &got, nullptr) || got == 0) {
                error = std::format("Could not read {}: {}", Text(from), LastErrorText(GetLastError()));
                return LoadFailure::Transient;
            }
            done += got;
        }

        std::error_code ec;
        std::filesystem::create_directories(to.parent_path(), ec);
        HandleCloser dst{ CreateFileW(to.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL,
            nullptr) };
        if (dst.h == INVALID_HANDLE_VALUE) {
            error = std::format("Could not create {}: {}", Text(to), LastErrorText(GetLastError()));
            return LoadFailure::Transient;
        }
        DWORD written = 0;
        done = 0;
        while (done < bytes.size()) {
            DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(bytes.size() - done, 1 << 20));
            if (!WriteFile(dst.h, bytes.data() + done, chunk, &written, nullptr) || written != chunk) {
                error = std::format("Could not write {}: {}", Text(to), LastErrorText(GetLastError()));
                return LoadFailure::Transient;
            }
            done += written;
        }
        return LoadFailure::None;
    }

    LogicLoader::LoadFailure LogicLoader::LoadModule(const std::filesystem::path& from, bool copyFirst,
        Module& out, std::string& error) {
        std::filesystem::path loadPath = from;

        if (copyFirst) {
            // Load a copy, never the file itself: a loaded DLL is locked, and the build
            // has to be able to overwrite it.
            loadPath = _shadowDir / std::format(L"{}{}-{}.dll", kShadowPrefix, GetCurrentProcessId(), ++_sequence);
            auto failure = CopyToShadow(from, loadPath, out.stamp, error);
            if (failure != LoadFailure::None) {
                DeleteFileW(loadPath.c_str());
                return failure;
            }
        }
        out.shadow = loadPath;

        HMODULE module = LoadLibraryW(loadPath.c_str());
        if (!module) {
            error = std::format("Could not load {}: {}", Text(from), LastErrorText(GetLastError()));
            if (copyFirst) DeleteFileW(loadPath.c_str());
            out.shadow.clear();
            return LoadFailure::Permanent;
        }
        out.handle = module;

        auto abiVersion = reinterpret_cast<SkyLinkAbiVersionFn>(GetProcAddress(module, "SkyLink_AbiVersion"));
        out.init = reinterpret_cast<SkyLinkInitFn>(GetProcAddress(module, "SkyLink_Init"));
        out.handle_request = reinterpret_cast<SkyLinkHandleRequestFn>(GetProcAddress(module, "SkyLink_HandleRequest"));
        out.get_info = reinterpret_cast<SkyLinkGetInfoFn>(GetProcAddress(module, "SkyLink_GetInfo"));
        out.shutdown = reinterpret_cast<SkyLinkShutdownFn>(GetProcAddress(module, "SkyLink_Shutdown"));

        if (!abiVersion || !out.init || !out.handle_request || !out.get_info || !out.shutdown) {
            error = std::format("{} is not a SkyLink logic DLL (missing exports)", Text(from.filename()));
            ReleaseModule(out, copyFirst);
            return LoadFailure::Permanent;
        }
        if (abiVersion() != SKYLINK_ABI_VERSION) {
            error = std::format("{} was built for plugin interface version {}, this plugin uses {}. "
                "Install SkyrimMCPPlugin.dll and SkyLinkLogic.dll from the same build.",
                Text(from.filename()), abiVersion(), SKYLINK_ABI_VERSION);
            ReleaseModule(out, copyFirst);
            return LoadFailure::Permanent;
        }

        std::string info;
        out.get_info(&AppendToString, &info);
        try {
            auto parsed = nlohmann::json::parse(info);
            out.version = parsed.value("version", "");
            out.built = parsed.value("built", "");
        } catch (...) {
            out.version = "unknown";
            out.built = "unknown";
        }
        return LoadFailure::None;
    }

    void LogicLoader::ReleaseModule(Module& module, bool deleteShadow) {
        if (module.handle) {
            FreeLibrary(static_cast<HMODULE>(module.handle));
        }
        if (deleteShadow && !module.shadow.empty()) {
            DeleteFileW(module.shadow.c_str());  // fails harmlessly if still mapped; cleaned up next session
        }
        module = Module{};
    }

    void LogicLoader::Postpone(const FileStamp& stamp, int seconds) {
        std::scoped_lock stampLock(_stampMutex);
        _postponedStamp = stamp;
        _retryAfterTicks = NowTicks() + static_cast<std::uint64_t>(seconds) * kTicksPerSecond;
    }

    bool LogicLoader::Load() {
        // Temp copies from earlier sessions. One still loaded by another running game
        // cannot be deleted, which is what we want.
        std::error_code ec;
        for (auto& entry : std::filesystem::directory_iterator(_shadowDir, ec)) {
            auto name = entry.path().filename().wstring();
            if (name.starts_with(kShadowPrefix) && entry.path().extension() == L".dll") {
                DeleteFileW(entry.path().c_str());
            }
        }

        std::unique_lock lock(_mutex);
        Module next;
        std::string error;
        if (LoadModule(_source, true, next, error) != LoadFailure::None) {
            _lastError = error;
            Log(kError, error);
            return false;
        }
        if (next.init(_host) != 0) {
            _lastError = std::format("{} failed to initialise", Text(_source.filename()));
            Log(kError, _lastError);
            ReleaseModule(next, true);
            return false;
        }

        _current = std::move(next);
        _loadedAt = LocalTimeNow();
        _lastError.clear();
        {
            std::scoped_lock stampLock(_stampMutex);
            _loadedStamp = _current.stamp;
        }
        Log(kInfo, std::format("Logic DLL loaded: v{} (built {}) from {}", _current.version, _current.built,
            Text(_source)));
        return true;
    }

    bool LogicLoader::Handle(const std::string& request, std::string& response) {
        std::shared_lock lock(_mutex);
        if (!_current.handle) return false;
        _current.handle_request(request.data(), request.size(), &AppendToString, &response);
        return true;
    }

    ReloadResult LogicLoader::Reload(bool onlyIfChanged, std::uint32_t drainMs) {
        ReloadResult result;
        const auto started = std::chrono::steady_clock::now();
        auto finish = [&](ReloadResult& r) -> ReloadResult& {
            r.elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - started).count();
            r.reloads = _reloads;
            if (!r.ok && !r.skipped) {
                _lastError = r.error;
                Log(kWarn, r.error);
            }
            return r;
        };

        // Waits for requests already running in the logic DLL; new ones wait for us.
        // From here on nothing can queue new game-thread work for the current build.
        std::unique_lock lock(_mutex);
        result.previousBuilt = _current.built;

        FileStamp onDisk;
        if (!StampOf(_source, onDisk)) {
            result.error = std::format("Logic DLL not found: {}", Text(_source));
            return finish(result);
        }
        if (onlyIfChanged && _current.handle && onDisk == _current.stamp) {
            result.skipped = true;
            result.built = _current.built;
            return finish(result);
        }

        // Game-thread tasks the current build queued (for example one whose request
        // already timed out) still call into it when the game runs them. Wait for them
        // before touching anything, so a postponed reload costs nothing but the wait.
        if (_current.handle) {
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(drainMs);
            while (_pendingTasks && _pendingTasks() > 0) {
                if (std::chrono::steady_clock::now() >= deadline) {
                    result.error = std::format("Reload postponed: {} game-thread task(s) from the current build "
                        "have not run yet (loading screen, or the game is paused?). It will be retried shortly.",
                        _pendingTasks());
                    Postpone(onDisk, 5);
                    return finish(result);
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(20));
            }
        }

        Module next;
        auto failure = LoadModule(_source, true, next, result.error);
        if (failure == LoadFailure::Transient) {
            Postpone(onDisk, 2);
            return finish(result);
        }
        if (failure == LoadFailure::Permanent) {
            std::scoped_lock stampLock(_stampMutex);
            _failedStamp = onDisk;  // don't retry this build on every request
            return finish(result);
        }

        Module previous = std::move(_current);
        _current = Module{};
        // ReleaseModule clears the Module, so keep what a restore would need.
        const std::filesystem::path previousShadow = previous.shadow;
        const FileStamp previousStamp = previous.stamp;
        if (previous.handle) {
            previous.shutdown();
            ReleaseModule(previous, false);  // keep its copy on disk until the new build is up
        }

        if (next.init(_host) != 0) {
            result.error = std::format("The new {} failed to initialise", Text(_source.filename()));
            ReleaseModule(next, true);
            {
                std::scoped_lock stampLock(_stampMutex);
                _failedStamp = onDisk;
            }
            // Bring the previous build back from its temp copy.
            std::string restoreError;
            if (!previousShadow.empty() &&
                LoadModule(previousShadow, false, _current, restoreError) == LoadFailure::None &&
                _current.init(_host) == 0) {
                _current.stamp = previousStamp;
                result.error += "; the previous build was restored";
                result.built = _current.built;
            } else {
                ReleaseModule(_current, true);
                result.error += "; the previous build could not be restored" +
                                (restoreError.empty() ? std::string() : " (" + restoreError + ")") +
                                ". Restart the game.";
            }
            return finish(result);
        }

        if (!previousShadow.empty()) DeleteFileW(previousShadow.c_str());

        _current = std::move(next);
        _loadedAt = LocalTimeNow();
        _lastError.clear();
        ++_reloads;
        {
            std::scoped_lock stampLock(_stampMutex);
            _loadedStamp = _current.stamp;
        }
        result.ok = true;
        result.built = _current.built;
        finish(result);
        Log(kInfo, std::format("Logic DLL reloaded in {} ms: v{} (built {}), previous build {}", result.elapsedMs,
            _current.version, _current.built, result.previousBuilt.empty() ? "none" : result.previousBuilt));
        return result;
    }

    bool LogicLoader::ShouldAutoReload() {
        FileStamp onDisk;
        if (!StampOf(_source, onDisk)) return false;
        const auto now = NowTicks();
        {
            std::scoped_lock stampLock(_stampMutex);
            if (onDisk == _loadedStamp || onDisk == _failedStamp) return false;
            if (onDisk == _postponedStamp && now < _retryAfterTicks) return false;
        }

        // A file being written has a last-write time of about "now" (equal to it, at the
        // clock's ~15 ms resolution). A time far in the future is a clock oddity, not a
        // copy in progress, so it does not block the reload forever.
        const bool farFuture = onDisk.lastWrite > now + 60 * kTicksPerSecond;
        if (!farFuture && now < onDisk.lastWrite + 2 * kTicksPerSecond) return false;

        // Still open for writing by the build or the copy: not finished. (Reload checks
        // again, atomically, when it copies the file.)
        HANDLE file = CreateFileW(_source.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        CloseHandle(file);
        return true;
    }

    LogicStatus LogicLoader::Status() {
        std::shared_lock lock(_mutex);
        LogicStatus status;
        status.loaded = _current.handle != nullptr;
        status.version = _current.version;
        status.built = _current.built;
        status.loadedAt = status.loaded ? _loadedAt : "";
        status.source = Text(_source);
        status.error = _lastError;
        status.reloads = _reloads;
        return status;
    }

}

// CommonLibSSE headers MUST come before Windows API
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include "CrashRecorder.h"

#include "BuildInfo.h"

#include <atomic>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <format>
#include <string>
#include <system_error>
#include <thread>

namespace SkyrimMCP::CrashRecorder {

    namespace {
        constexpr char kMagic[4] = { 'S', 'L', 'C', 'R' };
        constexpr std::uint32_t kFormatVersion = 1;
        constexpr std::uint32_t kCapacity = 512;

        constexpr std::uint32_t kExitRunning = 0;
        constexpr std::uint32_t kExitClean = 1;
        constexpr std::uint32_t kExitQuitting = 2;

        struct Header {
            char magic[4];
            std::uint32_t formatVersion;
            std::uint32_t headerSize;
            std::uint32_t entrySize;
            std::uint32_t capacity;
            std::uint32_t exitState;
            std::uint64_t sessionStartMs;
            std::uint64_t writeIndex;
            std::uint32_t processId;
            std::uint32_t reserved0;
            char pluginVersion[32];
            char gameVersion[32];
            std::uint64_t exitTimeMs;
            std::uint8_t reserved[136];
        };
        static_assert(sizeof(Header) == 256);
        static_assert(offsetof(Header, exitState) == 20);
        static_assert(offsetof(Header, sessionStartMs) == 24);
        static_assert(offsetof(Header, writeIndex) == 32);
        static_assert(offsetof(Header, processId) == 40);
        static_assert(offsetof(Header, pluginVersion) == 48);
        static_assert(offsetof(Header, gameVersion) == 80);
        static_assert(offsetof(Header, exitTimeMs) == 112);

        struct Entry {
            std::uint64_t timeMs;
            std::uint32_t threadId;
            std::uint8_t kind;
            std::uint8_t reserved[3];
            std::uint64_t sequence;
            char text[kTextSize];
        };
        static_assert(sizeof(Entry) == 256);
        static_assert(offsetof(Entry, kind) == 12);
        static_assert(offsetof(Entry, sequence) == 16);
        static_assert(offsetof(Entry, text) == 24);

        constexpr std::size_t kFileSize = sizeof(Header) + sizeof(Entry) * kCapacity;

        // Set once Init has mapped and initialised the file; never unmapped (the host DLL
        // lives for the whole process, and the OS writes the pages back when it ends).
        std::atomic<std::byte*> g_base{ nullptr };

        std::uint64_t NowUnixMs() noexcept {
            FILETIME ft;
            GetSystemTimeAsFileTime(&ft);
            ULARGE_INTEGER t;
            t.LowPart = ft.dwLowDateTime;
            t.HighPart = ft.dwHighDateTime;
            // FILETIME counts 100 ns intervals since 1601-01-01.
            return (t.QuadPart - 116444736000000000ULL) / 10000ULL;
        }

        void CopyText(char* dest, std::size_t size, std::string_view text) noexcept {
            std::size_t n = text.size() < size - 1 ? text.size() : size - 1;
            for (std::size_t i = 0; i < n; ++i) {
                char c = text[i];
                dest[i] = (c == '\n' || c == '\r' || c == '\0') ? ' ' : c;
            }
            dest[n] = '\0';
        }

        void SetExitState(std::uint32_t state) noexcept {
            auto* base = g_base.load(std::memory_order_acquire);
            if (!base) return;
            auto* header = reinterpret_cast<Header*>(base);
            // A clean exit is final; the quit watcher must not downgrade it.
            if (state != kExitClean && header->exitState == kExitClean) return;
            header->exitTimeMs = NowUnixMs();
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&header->exitState), static_cast<LONG>(state));
            FlushViewOfFile(base, 0);
        }

        // Registered with atexit: in a DLL that runs at DLL_PROCESS_DETACH when the process
        // exits normally (ExitProcess). Not on a crash or TerminateProcess. No logging here:
        // spdlog may already be torn down.
        void OnProcessExit() {
            SetExitState(kExitClean);
        }

        std::string Text(const std::filesystem::path& path) {
            auto u8 = path.u8string();
            return std::string(u8.begin(), u8.end());
        }

        // Reads the previous session's header; true if it is ours and did not exit cleanly.
        bool PreviousSessionCrashed(const std::filesystem::path& file) {
            HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h == INVALID_HANDLE_VALUE) return false;
            Header header{};
            DWORD read = 0;
            BOOL ok = ReadFile(h, &header, sizeof(header), &read, nullptr);
            CloseHandle(h);
            if (!ok || read != sizeof(header)) return false;
            if (std::memcmp(header.magic, kMagic, sizeof(kMagic)) != 0) return false;
            return header.exitState != kExitClean && header.writeIndex > 0;
        }
    }

    bool Enabled() noexcept {
        return g_base.load(std::memory_order_acquire) != nullptr;
    }

    void Record(Kind kind, std::string_view text) noexcept {
        auto* base = g_base.load(std::memory_order_acquire);
        if (!base) return;
        auto* header = reinterpret_cast<Header*>(base);
        auto* entries = reinterpret_cast<Entry*>(base + sizeof(Header));

        const auto index = static_cast<std::uint64_t>(
            InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&header->writeIndex)) - 1);
        Entry& entry = entries[index % kCapacity];

        // Sequence 0 while the slot is being rewritten, the real one only once it is complete.
        InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&entry.sequence), 0);
        entry.timeMs = NowUnixMs();
        entry.threadId = GetCurrentThreadId();
        entry.kind = static_cast<std::uint8_t>(kind);
        CopyText(entry.text, sizeof(entry.text), text);
        InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&entry.sequence), static_cast<LONG64>(index + 1));
    }

    void Init() {
        if (Enabled()) return;
        try {
            auto logDir = SKSE::log::log_directory();
            if (!logDir) {
                SKSE::log::warn("Crash recorder off: could not find the My Games folder");
                return;
            }
            // log_directory() is <My Games>\<game folder>\SKSE; its parent also covers GOG and VR.
            const auto dir = logDir->parent_path() / L"SkyLinkAI";
            std::error_code ec;
            std::filesystem::create_directories(dir, ec);
            if (ec) {
                SKSE::log::warn("Crash recorder off: could not create {}: {}", Text(dir), ec.message());
                return;
            }
            const auto current = dir / L"crash-recorder.bin";
            const auto previous = dir / L"crash-recorder-previous.bin";

            // Keep the evidence from a session that crashed before this one overwrites it.
            if (PreviousSessionCrashed(current)) {
                // FEAT-264: keep the last five unclean sessions (previous, previous-2 ... previous-5), so a short false start or a
                // launch that died with Steam doesn't overwrite the record of a real crash.
                for (int n = 4; n >= 1; --n) {
                    const auto from = n == 1 ? previous : dir / std::format(L"crash-recorder-previous-{}.bin", n);
                    const auto to = dir / std::format(L"crash-recorder-previous-{}.bin", n + 1);
                    std::error_code mv;
                    if (std::filesystem::exists(from, mv)) std::filesystem::copy_file(from, to, std::filesystem::copy_options::overwrite_existing, mv);
                }
                if (CopyFileW(current.c_str(), previous.c_str(), FALSE)) {
                    SKSE::log::info("Crash recorder: the last session did not exit cleanly; kept its record as {}",
                        Text(previous));
                } else {
                    SKSE::log::warn("Crash recorder: could not copy the last session's record to {} (error {})",
                        Text(previous), GetLastError());
                }
            }

            HANDLE file = CreateFileW(current.c_str(), GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) {
                SKSE::log::warn("Crash recorder off: could not create {} (error {})", Text(current), GetLastError());
                return;
            }
            HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, static_cast<DWORD>(kFileSize), nullptr);
            if (!mapping) {
                SKSE::log::warn("Crash recorder off: could not map {} (error {})", Text(current), GetLastError());
                CloseHandle(file);
                return;
            }
            void* view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, kFileSize);
            if (!view) {
                SKSE::log::warn("Crash recorder off: could not map a view of {} (error {})", Text(current), GetLastError());
                CloseHandle(mapping);
                CloseHandle(file);
                return;
            }
            // The handles stay open for the whole process, like the view.

            auto* base = static_cast<std::byte*>(view);
            std::memset(base, 0, kFileSize);
            auto* header = reinterpret_cast<Header*>(base);
            std::memcpy(header->magic, kMagic, sizeof(kMagic));
            header->formatVersion = kFormatVersion;
            header->headerSize = sizeof(Header);
            header->entrySize = sizeof(Entry);
            header->capacity = kCapacity;
            header->exitState = kExitRunning;
            header->sessionStartMs = NowUnixMs();
            header->writeIndex = 0;
            header->processId = GetCurrentProcessId();
            CopyText(header->pluginVersion, sizeof(header->pluginVersion), SKYLINK_VERSION_STRING);
            CopyText(header->gameVersion, sizeof(header->gameVersion), REL::Module::get().version().string("."));
            FlushViewOfFile(base, 0);

            g_base.store(base, std::memory_order_release);
            std::atexit(&OnProcessExit);

            Recordf(Kind::Note, "SkyLink host {} loaded (built {}), game {}, process {}", SKYLINK_VERSION_STRING,
                BuildInfo::GetBuildTime(), static_cast<const char*>(header->gameVersion),
                static_cast<std::uint32_t>(header->processId));
            SKSE::log::info("Crash recorder writing to {}", Text(current));
        } catch (const std::exception& e) {
            SKSE::log::warn("Crash recorder off: {}", e.what());
        } catch (...) {
            SKSE::log::warn("Crash recorder off: unexpected error");
        }
    }

    namespace {
        BOOL CALLBACK FindWindowOfProcess(HWND hwnd, LPARAM out) {
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (pid != GetCurrentProcessId() || !IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;
            *reinterpret_cast<HWND*>(out) = hwnd;
            return FALSE;
        }

        // The game's main window: the visible top-level window of this process.
        HWND FindGameWindow() {
            HWND found = nullptr;
            EnumWindows(&FindWindowOfProcess, reinterpret_cast<LPARAM>(&found));
            return found;
        }
    }

    void StartQuitWatcher() {
        if (!Enabled()) return;
        try {
            // Skyrim may end the process without a normal exit after the player quits, so
            // atexit alone could label every session a crash. The game sets Main::quitGame
            // when the player quits to the desktop; once it is set, the session is marked
            // "quitting" (exit state 2). A normal exit then upgrades that to clean.
            // BUG-063: a normal quit on 1.7.104 was recorded as "did not exit cleanly": neither quitGame nor the atexit
            // handler fired. The game's window going away is a second sign of quitting that doesn't depend on a field
            // offset; a crash leaves the window in place until the process dies.
            std::thread([]() {
                HWND window = nullptr;
                for (;;) {
                    Sleep(250);
                    if (!window) window = FindGameWindow();
                    auto* main = RE::Main::GetSingleton();
                    if (main && main->GetRuntimeData().quitGame) {
                        Record(Kind::Note, "The game is quitting (Main::quitGame set)");
                        SetExitState(kExitQuitting);
                        return;
                    }
                    if (window && !IsWindow(window)) {
                        Record(Kind::Note, "The game is quitting (its window closed)");
                        SetExitState(kExitQuitting);
                        return;
                    }
                }
            }).detach();
        } catch (...) {
            SKSE::log::warn("Crash recorder: could not start the quit watcher; a normal quit may look like a crash");
        }
    }

}

#pragma once

// Crash recorder (FEAT-219, host DLL only): what SkyLink and the game did just before a crash.
//
// The ring buffer is a memory-mapped file, so its pages reach disk even when the process
// dies: no exception handler is installed, and Crash Logger SSE handles the crash as usual.
// On the next launch the MCP server's GetCrashLog reads it next to the Crash Logger report.
//
//   <My Games>\<game folder>\SkyLinkAI\crash-recorder.bin           this session
//   <My Games>\<game folder>\SkyLinkAI\crash-recorder-previous.bin  the last session that
//                                                                   did not exit cleanly
//
// File format (little-endian, version 1). SkyrimMCP/Tools/CrashTools.cs decodes it: keep
// the two in step.
//   header, 256 bytes:
//     0   char[4]  magic "SLCR"
//     4   u32      format version (1)
//     8   u32      header size (256)
//     12  u32      entry size (256)
//     16  u32      capacity (number of entries, 512)
//     20  u32      exit state: 0 = running or crashed, 1 = clean exit (process exited
//                  normally), 2 = the game had started quitting but the process did not
//                  reach a normal exit (crash on exit, or the game ended itself abruptly)
//     24  u64      session start, UTC unix milliseconds
//     32  u64      write index: entries ever written; the next goes to slot index % capacity
//     40  u32      process id
//     44  u32      reserved
//     48  char[32] plugin version, NUL-terminated
//     80  char[32] game runtime version, NUL-terminated
//     112 u64      UTC unix milliseconds when the exit state was set (0 while running)
//     120          reserved, zero
//   entries, 256 bytes each, from offset 256:
//     0   u64      UTC unix milliseconds
//     8   u32      thread id
//     12  u8       kind (see Kind)
//     13  u8[3]    reserved
//     16  u64      sequence number (write index + 1; 0 = slot never written). Written last,
//                  so an entry torn by a crash mid-write still has its old sequence.
//     24  char[232] text, UTF-8, NUL-terminated

#include <cstddef>
#include <cstdint>
#include <format>
#include <string_view>
#include <utility>

namespace SkyrimMCP::CrashRecorder {

    enum class Kind : std::uint8_t {
        Request = 1,  // a pipe request: "> action params" when it starts, "< action ok/error" when done
        Event = 2,    // a game event from EventSystem
        Cell = 3,     // the player changed cell or location
        Papyrus = 4,  // a Papyrus VM call made through SkyLink
        Note = 5,     // plugin load, logic DLL load/reload, loading screens, save/load messages
    };

    inline constexpr std::size_t kTextSize = 232;

    // From SKSEPluginLoad, after logging and SKSE::Init. Creates the file (after keeping
    // the previous session's file if that session did not exit cleanly). On any failure
    // it logs a warning and recording stays off. Never throws.
    void Init();

    // On kDataLoaded: starts the watcher that marks the session as quitting once the game
    // sets its quit flag. Never throws.
    void StartQuitWatcher();

    bool Enabled() noexcept;

    // Lock-free and allocation-free; safe from any thread. Text longer than the entry is
    // truncated; newlines become spaces. No-op when recording is off.
    void Record(Kind kind, std::string_view text) noexcept;

    // Formats into a stack buffer, then Record.
    template <class... Args>
    void Recordf(Kind kind, std::format_string<Args...> fmt, Args&&... args) noexcept {
        if (!Enabled()) return;
        try {
            char buffer[kTextSize];
            auto result = std::format_to_n(buffer, static_cast<std::ptrdiff_t>(kTextSize - 1), fmt, std::forward<Args>(args)...);
            Record(kind, std::string_view(buffer, static_cast<std::size_t>(result.out - buffer)));
        } catch (...) {
        }
    }

}

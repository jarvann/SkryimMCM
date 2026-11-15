// CommonLibSSE headers MUST come before Windows API
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include "BuildInfo.h"

#include <ctime>

namespace SkyrimMCP::BuildInfo {

    static std::string ReadLinkTime() {
        HMODULE module = nullptr;
        if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                reinterpret_cast<LPCSTR>(&ReadLinkTime), &module) || !module) {
            return "unknown";
        }

        auto* base = reinterpret_cast<const std::byte*>(module);
        auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);

        std::time_t linked = static_cast<std::time_t>(nt->FileHeader.TimeDateStamp);
        std::tm utc{};
        if (gmtime_s(&utc, &linked) != 0) return "unknown";

        char text[32];
        std::strftime(text, sizeof(text), "%Y-%m-%d %H:%M:%S UTC", &utc);
        return text;
    }

    const std::string& GetBuildTime() {
        static const std::string buildTime = ReadLinkTime();
        return buildTime;
    }
}

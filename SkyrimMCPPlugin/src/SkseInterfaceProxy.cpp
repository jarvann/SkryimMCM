// CommonLibSSE headers MUST come before Windows API
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include "SkseInterfaceProxy.h"

#include <array>
#include <cstddef>

namespace SkyrimMCP::SkseInterfaceProxy {

    namespace {
        using Table = SKSE::Impl::SKSEInterface;  // SKSE/Interfaces.h

        // The copy has to match SKSE's struct field for field. CommonLib's definition is
        // the one this build compiles against; these checks catch a CommonLib update that
        // changes it. tests/ReloadHarness.cpp checks its fake table against the same size.
        static_assert(sizeof(Table) == 48);
        static_assert(offsetof(Table, QueryInterface) == 16);
        static_assert(offsetof(Table, GetPluginHandle) == 24);
        static_assert(offsetof(Table, GetReleaseIndex) == 32);
        static_assert(offsetof(Table, GetPluginInfo) == 40);

        // Indexed by LoadInterface id (kScaleform .. kTrampoline); slot 0 is kInvalid.
        std::array<void*, SKSE::LoadInterface::kTotal> g_interfaces{};
        SKSE::PluginHandle g_pluginHandle = static_cast<SKSE::PluginHandle>(-1);  // SKSE's "invalid handle"
        std::uint32_t g_releaseIndex = 0;
        Table g_table{};
        bool g_captured = false;

        void* QueryInterface(std::uint32_t id) {
            return id < g_interfaces.size() ? g_interfaces[id] : nullptr;
        }
        SKSE::PluginHandle GetPluginHandle() { return g_pluginHandle; }
        std::uint32_t GetReleaseIndex() { return g_releaseIndex; }
    }

    void Capture(const SKSE::LoadInterface* skse) {
        if (!skse || g_captured) return;

        // LoadInterface is CommonLib's typed view of this same struct.
        const auto& real = *reinterpret_cast<const Table*>(skse);

        g_pluginHandle = real.GetPluginHandle();
        g_releaseIndex = real.GetReleaseIndex();
        std::size_t found = 0;
        for (std::uint32_t id = 1; id < g_interfaces.size(); ++id) {
            g_interfaces[id] = real.QueryInterface(id);
            found += g_interfaces[id] != nullptr;
        }

        g_table.skseVersion = real.skseVersion;
        g_table.runtimeVersion = real.runtimeVersion;
        g_table.editorVersion = real.editorVersion;
        g_table.isEditor = real.isEditor;
        g_table.QueryInterface = &QueryInterface;
        g_table.GetPluginHandle = &GetPluginHandle;
        g_table.GetReleaseIndex = &GetReleaseIndex;
        g_table.GetPluginInfo = real.GetPluginInfo;
        g_captured = true;

        SKSE::log::info("Captured the SKSE interface table for the logic DLL (plugin handle {}, {} of {} interfaces)",
            g_pluginHandle, found, g_interfaces.size() - 1);
    }

    const SKSE::LoadInterface* Get() {
        return g_captured ? reinterpret_cast<const SKSE::LoadInterface*>(&g_table) : nullptr;
    }

}

#pragma once

// A copy of SKSE's plugin interface table for the logic DLL (host only).
//
// SKSE allows GetPluginHandle only inside a plugin's own SKSEPluginLoad. Called any later,
// it fails an assertion that crashes the game on purpose. The logic DLL runs SKSE::Init
// much later: on kDataLoaded, and again in each new copy after a hot reload. So the host
// captures SKSE's answers while it is loading and gives the logic DLL this copy instead.
//
//   GetPluginHandle, GetReleaseIndex, QueryInterface: answered from values captured at load
//   version fields:                                   copied
//   GetPluginInfo:                                    SKSE's own function (safe at any time)

namespace SKSE {
    class LoadInterface;
}

namespace SkyrimMCP::SkseInterfaceProxy {

    // Call once, from SKSEPluginLoad, before anything uses Get().
    void Capture(const SKSE::LoadInterface* skse);

    // The copy to pass to the logic DLL's SKSE::Init, or null if Capture never ran.
    const SKSE::LoadInterface* Get();

}

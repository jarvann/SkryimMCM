#pragma once

// The host side of the plugin (see HostApi.h): owns the logic-DLL loader, answers the
// few requests that must work whatever state the logic DLL is in, and passes every
// other request to the logic DLL.

#include <string>

namespace SKSE {
    class LoadInterface;
}

namespace SkyrimMCP::HostCore {

    // From SKSEPluginLoad. SKSE keeps this interface alive for the whole session.
    void SetLoadInterface(const SKSE::LoadInterface* skse);

    // On kDataLoaded, before the pipe server starts: loads the logic DLL.
    void Start();

    // One newline-delimited JSON request from the pipe, one JSON response line back.
    //   ping           answered here, with the host and logic DLL versions
    //   poll_events    answered here: the event sinks live in the host
    //   reload_plugin  swaps in the logic DLL now on disk
    // Anything else goes to the logic DLL, which is first reloaded automatically if a
    // newer build has been deployed.
    std::string HandleRequest(const std::string& requestLine);

}

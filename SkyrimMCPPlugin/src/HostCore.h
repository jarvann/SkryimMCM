#pragma once

// The host side of the plugin (see HostApi.h): owns the logic-DLL loader, answers the
// few requests that must work whatever state the logic DLL is in, and passes every
// other request to the logic DLL.

#include <string>

namespace SkyrimMCP::HostCore {

    // On kDataLoaded, before the pipe server starts: loads the logic DLL.
    void Start();

    // One newline-delimited JSON request from the pipe, one JSON response line back.
    //   ping             answered here, with the host and logic DLL versions (after
    //                    loading a newer logic DLL, if one has been deployed)
    //   poll_events      answered here: the event sinks live in the host
    //   reload_plugin    swaps in the logic DLL now on disk
    //   get_game_safety  answered here during a loading screen (the game thread is busy)
    // Anything else goes to the logic DLL, which is first reloaded automatically if a
    // newer build has been deployed.
    std::string HandleRequest(const std::string& requestLine);

    // FEAT-268: counts save loads and new games, so the server can tell one game state from the next.
    void NoteGameLoaded();

    // Whether a loading screen is up (the game thread is busy loading, so game-thread requests can't run).
    bool LoadingScreenActive();

}

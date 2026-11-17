#pragma once

#include <nlohmann/json.hpp>

namespace SkyrimMCP::FaceState {
    using json = nlohmann::json;

    // FEAT-249: puts back what SkyLink keeps per character in Documents\My Games\Skyrim Special Edition\SkyLinkAI\
    // face_state.json (written by the server's FaceState) that neither the game nor RaceMenu restores: the brow colour.
    // The host calls it after a save loads and after the player's cell changes.
    json Reapply();
}

#pragma once

#include <nlohmann/json.hpp>
#include <cstdint>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Face studio (FEAT-244): controlled light for face captures, so colours can be compared between captures.
    // Lights are engine-level point lights (NiPointLight added to the shadow scene, attached to the player's 3D),
    // not placed objects: nothing goes into the save. Optionally the player is moved to an interior (fixed
    // ambient light, no sun or weather) and brought back to an exact marked spot.
    namespace Studio {
        // Puts the preset's lights around the player's head (replacing any studio lights): neutral, warm,
        // daylight, soft, or off (removes them). Returns where each light is. refId: light that actor instead (FEAT-122).
        json Lights(const std::string& preset, float intensity, const std::string& refId = "");

        // Marks the player's spot (a persistent XMarker with her facing), then moves her to an interior cell by
        // editor ID. Refuses in combat or when a spot is already marked (call Return first).
        json Go(const std::string& cellEditorId);

        // Removes the lights and moves the player back to the marked spot with her facing.
        json Return(std::uint32_t markerId);

        // Deletes the marker once the player is back (call after the load has finished).
        json Cleanup(std::uint32_t markerId);

        // XMarkers placed at run time, newest first (to recover a studio trip whose marker ID was lost).
        json FindMarkers();

        // Lights on, preset, marked spot, current cell.
        json Status();
    }
}

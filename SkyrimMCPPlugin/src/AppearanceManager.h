#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Appearance changes made with the game's own data, so they work without RaceMenu
    // (FEAT-226). RaceMenu support (FEAT-227) builds on these.
    namespace AppearanceManager {
        // Hair colour and skin tone of an actor (the player when refId is empty).
        json GetColors(const std::string& refId);

        // Hair colour forms (CLFM) in the load order, with their RGB values.
        json ListHairColors(bool includeNonPlayable);

        // color: a hair colour form ID, a name or editor ID, "#RRGGBB" or "r,g,b".
        // An RGB value picks the closest existing hair colour form, so the change survives
        // saving and loading.
        json SetHairColor(const std::string& refId, const std::string& color);

        // color: "#RRGGBB" or "r,g,b". refresh rebuilds the actor's 3D (needed for the face).
        json SetSkinTone(const std::string& refId, const std::string& color, bool refresh);
    }

}

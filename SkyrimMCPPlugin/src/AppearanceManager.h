#pragma once

#include <cstddef>
#include <nlohmann/json.hpp>
#include <string>
#include <vector>

namespace RE {
    class BGSHeadPart;
    class TESNPC;
}

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

        // FEAT-209: the head parts a character menu offers for one type (playable, this sex,
        // valid for this race, not an extra part), in one of the candidate orders:
        // "editorId" (byte order, case-sensitive: RaceMenu's slider order, confirmed in game),
        // "editorIdNoCase", "name", "loadOrder".
        inline constexpr const char* kHeadPartOrders[] = { "editorId", "editorIdNoCase", "name", "loadOrder" };
        inline constexpr std::size_t kHeadPartOrderCount = 4;
        std::vector<RE::BGSHeadPart*> HeadPartChoices(RE::TESNPC* npc, int type, const std::string& order);

        // A page of that list, or the entries around one part: for checking against RaceMenu.
        json ListHeadPartChoices(const std::string& refId, const std::string& type, const std::string& order,
                                 int start, int count, const std::string& aroundFormId);

        // FEAT-208: change head parts. part is a FormID or editor ID; or type + index (menu order).
        // Misc parts are added (several can be worn), other types replace the current part.
        json SetHeadPart(const std::string& refId, const std::string& part, const std::string& type, int index, bool refresh);
        // Removes one part (by FormID/editor ID, or the current part of a type). Not face or eyes.
        json RemoveHeadPart(const std::string& refId, const std::string& part, const std::string& type, bool refresh);
    }

}

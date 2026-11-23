#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace WorldManager {
        json Teleport(const std::string& cellId, const std::string& method = "auto");
        json GetWeather();
        json SetWeather(const std::string& weatherFormIdOrName);
        json ListWeathers();
        json GetCellInfo();
        json GetNearbyObjects(float radius, const std::string& typeFilter);
        json UnlockDoor(const std::string& refFormIdHex);
        json LockDoor(const std::string& refFormIdHex, int lockLevel);
        json GetContainerInventory(const std::string& refFormIdHex);
        json DiscoverAllMapMarkers();
        // FEAT-307 prototype: flat, uncluttered ground near the player for a home (land heights and nearby objects), and placing
        // a base object there for good (persistent, so saves keep it).
        json FindOpenSpace(float searchRadius, float size);
        json PlaceObject(const std::string& baseId, float x, float y, float z, float angleZ, bool onGround);
        json RemoveObject(const std::string& refId);
        json OwnedHouses();
        json AddMapMarker(const std::string& atRef, const std::string& name, int type);
        // Objects placed at run time around a centre reference: base (stable ID), offset from the centre, angle; for home layouts.
        json PlacedObjects(const std::string& centerRef, float radius);
        json GetGameTime();
        json SetGameTime(float hours);
        json ToggleCollision(const std::string& refFormIdHex = "");

        // Economy (Phase 7)
        json GetNearbyMerchants(float radius);
        json GetMerchantInventory(const std::string& refFormIdHex);
        json GetBounties();
        json ClearBounty(const std::string& factionFormIdHex);

        // Music (native API)
        json PlayMusic(const std::string& formIdHex);
        json StopMusic();
    }

}

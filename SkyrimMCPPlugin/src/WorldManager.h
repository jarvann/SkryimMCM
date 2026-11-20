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

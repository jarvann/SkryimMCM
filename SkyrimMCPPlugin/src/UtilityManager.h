#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace UtilityManager {
        json ShowNotification(const std::string& message);
        json SearchForms(const std::string& query, const std::string& formTypeFilter, int maxResults);
        json GetLoadedSKSEPlugins();
        json SaveGame(const std::string& saveName);
        json GetLoadOrder();
        json GetModFormIdPrefix(const std::string& modName);
        json LoadSave(const std::string& saveName);
        json LoadMostRecentSave();
        json GetSessionState();
        json QuitGame();
        json ReloadContent();
        // FEAT-131: an Audio menu category's volume (0-1); category "master" (default), a menu
        // category's name, or "list".
        json SetVolume(const std::string& category, float volume);
        // FEAT-133: queue a screenshot with the game's Print Screen handler.
        json TakeScreenshot();
    }

}

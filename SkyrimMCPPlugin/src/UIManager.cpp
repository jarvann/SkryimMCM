#include "UIManager.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

namespace SkyrimMCP::UIManager {

    using json = nlohmann::json;

    json GetMenuState() {
        auto* ui = RE::UI::GetSingleton();
        if (!ui) return {{"error", "UI not available"}};

        json result;
        result["gameIsPaused"] = ui->GameIsPaused();

        // Check common menus
        static const char* menus[] = {
            "Console", "InventoryMenu", "MagicMenu", "MapMenu",
            "StatsMenu", "ContainerMenu", "DialogueMenu", "BarterMenu",
            "FavoritesMenu", "Journal Menu", "Book Menu", "LockpickingMenu",
            "Loading Menu", "MessageBoxMenu", "TweenMenu", "Sleep/Wait Menu",
            "Crafting Menu", "Training Menu", "RaceSex Menu"
        };

        json openMenus = json::array();
        for (auto* menu : menus) {
            if (ui->IsMenuOpen(menu)) {
                openMenus.push_back(menu);
            }
        }
        result["openMenus"] = openMenus;
        result["menuCount"] = openMenus.size();

        // INFRA-017: openMenus only checks the vanilla menus above, so HUD overlays and mod
        // menus (SkyUI widgets, TrueHUD, etc.) never showed. allOpenMenus lists every menu the
        // game has open, from its own menu table, with what each one does.
        json all = json::array();
        for (auto& [name, entry] : ui->menuMap) {
            const auto& menu = entry.menu;
            if (!menu || !menu->OnStack()) continue;
            json m;
            m["name"] = name.c_str();
            m["pausesGame"] = menu->PausesGame();
            m["usesCursor"] = menu->UsesCursor();
            m["modal"] = menu->Modal();
            m["alwaysOpen"] = menu->AlwaysOpen();  // HUD-style overlays
            all.push_back(std::move(m));
        }
        result["allOpenMenus"] = all;

        return result;
    }

}

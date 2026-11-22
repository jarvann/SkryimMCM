#include "UIManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <format>
#include <functional>
#include <string>

namespace SkyrimMCP::UIManager {

    using json = nlohmann::json;

    namespace {
        // What is on screen and where it sits in the queue: another box, or the same text asked again, gets another ID.
        std::string BoxId(const RE::MessageBoxData* data, const std::string& text) {
            return std::format("{:08X}-{:X}", static_cast<std::uint32_t>(std::hash<std::string>{}(text)), reinterpret_cast<std::uintptr_t>(data) & 0xFFFFFFF);
        }
        std::string Utf8(const RE::BSString& s) { return Helpers::Utf8(s.c_str() ? s.c_str() : ""); }
    }

    json GetMessageBox() {
        auto* ui = RE::UI::GetSingleton();
        if (!ui || !ui->IsMenuOpen(RE::MessageBoxMenu::MENU_NAME)) return {{"open", false}};
        auto* data = RE::MessageBoxMenu::GetCurrentMessageBoxData();
        if (!data) return {{"open", true}, {"readable", false}, {"note", "A message box is open but its text can't be read (a mod may draw its own)"}};
        json buttons = json::array();
        for (const auto& b : data->buttonText) buttons.push_back(Utf8(b));
        const auto text = Utf8(data->bodyText);
        return {{"open", true}, {"readable", true}, {"boxId", BoxId(data, text)}, {"text", text}, {"buttons", buttons},
                {"cancelButton", data->cancelButtonIndex}, {"waiting", RE::MessageBoxMenu::GetQueueSize()}};
    }

    json AnswerMessageBox(const std::string& boxId, int button) {
        auto* ui = RE::UI::GetSingleton();
        auto* data = ui && ui->IsMenuOpen(RE::MessageBoxMenu::MENU_NAME) ? RE::MessageBoxMenu::GetCurrentMessageBoxData() : nullptr;
        if (!data) return {{"error", "No message box is open"}};
        const auto text = Utf8(data->bodyText);
        if (BoxId(data, text) != boxId) return {{"error", "The box on screen isn't the one that was read: read it again before answering"}, {"now", text}};
        const int count = static_cast<int>(data->buttonText.size());
        if (button < 0 || button >= count) return {{"error", std::format("Button {} doesn't exist (0 to {})", button, count - 1)}};
        const auto label = Utf8(data->buttonText[button]);
        RE::MessageBoxMenu::SelectOption(button);
        return {{"answered", label}, {"button", button}, {"text", text}};
    }

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
        // BUG-081: a mod's own menu (CustomMenu, used by MCM Recorder, Skyrim Unbound, custom skill pickers...) that takes the
        // cursor counts as open too; the summary said none were open while one had the cursor.
        for (auto& [name, entry] : ui->menuMap) {
            const auto& menu = entry.menu;
            if (!menu || !menu->OnStack() || menu->AlwaysOpen() || !menu->UsesCursor()) continue;
            const std::string n = name.c_str();
            if (n == "Cursor Menu" || n == "HUD Menu" || n == "Fader Menu") continue;
            if (std::find(openMenus.begin(), openMenus.end(), json(n)) == openMenus.end()) openMenus.push_back(n);
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

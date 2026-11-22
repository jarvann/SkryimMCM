#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace UIManager {
        json GetMenuState();
        // FEAT-302: the message box on screen: its text, its buttons and an ID that changes with the box.
        json GetMessageBox();
        // Presses one of its buttons, only while it is still the box with that ID.
        json AnswerMessageBox(const std::string& boxId, int button);
    }

}

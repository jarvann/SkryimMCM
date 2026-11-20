#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace SkyrimMCP {

    using json = nlohmann::json;

    namespace QuestManager {
        json GetQuestInfo();
        json GetQuestStage(const std::string& formIdHex);
        json GetQuestStages(const std::string& formIdHex);
        json SetQuestStage(const std::string& formIdHex, int stage);
        json CompleteQuest(const std::string& formIdHex);
        json StartQuest(const std::string& formIdHex);
        json StopQuest(const std::string& formIdHex);
        json GetQuestAliases(const std::string& formIdHex);
        json GetQuestItems();
        // FEAT-113: the journal's log entries per quest (questFilter: form ID, editor ID or part of the name; empty = all).
        json GetJournal(const std::string& questFilter, int maxQuests, bool includeHidden);
    }

}

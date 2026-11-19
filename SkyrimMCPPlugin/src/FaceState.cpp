#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include "AppearanceManager.h"
#include "FaceState.h"
#include "Helpers.h"

#include <ShlObj.h>

#include <filesystem>
#include <fstream>
#include <string>

namespace SkyrimMCP::FaceState {

    namespace {
        std::filesystem::path StateFile() {
            PWSTR documents = nullptr;
            std::filesystem::path path;
            // The same folder the server uses (Environment.SpecialFolder.MyDocuments, which follows OneDrive redirection).
            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Documents, 0, nullptr, &documents)) && documents) {
                path = std::filesystem::path(documents) / "My Games" / "Skyrim Special Edition" / "SkyLinkAI" / "face_state.json";
                // BUG-088: the running game's own folder (the GOG game has its own).
                if (auto game = Helpers::GameDocumentsFolder(); !game.empty()) path = game / "SkyLinkAI" / "face_state.json";
            }
            if (documents) CoTaskMemFree(documents);
            return path;
        }
    }

    json Reapply() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* npc = player ? player->GetActorBase() : nullptr;
        if (!npc || !player->Get3D(false)) return {{"skipped", "the player's 3D isn't loaded"}};

        const auto file = StateFile();
        std::error_code ec;
        if (file.empty() || !std::filesystem::exists(file, ec)) return {{"skipped", "no face_state.json"}};
        json all;
        try {
            std::ifstream in(file);
            all = json::parse(in);
        } catch (const std::exception& e) {
            return {{"error", std::string("face_state.json unreadable: ") + e.what()}};
        }

        // The key the server writes (FaceState.Key): "id:<character ID>" (BUG-090). Data from before is under "name|race" until
        // the server moves it; read that only if it hasn't been moved to another character.
        const char* name = player->GetName();
        const char* race = npc->GetRace() ? npc->GetRace()->GetName() : nullptr;
        const auto characterId = Helpers::CharacterId();
        if (characterId.empty()) return {{"skipped", "no character ID yet"}};
        std::string key = "id:" + characterId;
        if (!all.contains(key) || !all[key].is_object()) {
            const std::string legacy = std::string(name ? name : "") + "|" + (race ? race : "");
            const bool usable = all.contains(legacy) && all[legacy].is_object() &&
                                (!all[legacy].contains("migratedTo") || all[legacy]["migratedTo"] == characterId);
            if (!usable) return {{"skipped", "nothing stored for " + key}};
            key = legacy;
        }
        const auto& entry = all[key];

        json result = {{"character", key}};
        if (entry.contains("browColor") && entry["browColor"].is_string()) {
            result["browColor"] = AppearanceManager::HeadPartTint(6, entry["browColor"].get<std::string>(), false);
        }
        return result;
    }
}

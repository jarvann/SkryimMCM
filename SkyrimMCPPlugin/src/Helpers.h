#pragma once

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <excpt.h>
#include <format>
#include <stdexcept>
#include <cctype>
#include <algorithm>
#include <mutex>
#include <optional>
#include <string>
#include <unordered_map>

#include <nlohmann/json.hpp>

namespace SkyrimMCP::Helpers {

    using json = nlohmann::json;

    // ==================== Form ID / Actor Value Helpers ====================

    // Strict: the whole string must be 1-8 hex digits (optional 0x, surrounding spaces allowed).
    // std::stoul alone stops at the first non-hex character, so a name like "Forsworn" parsed as
    // 0xF (gold) and "Beef" as 0xBEEF: TransferItems then matched the player's gold (2026-11-16).
    // Why the player can't be moved now (an open pausing menu, a mount, a bed or chair), or empty when she can (BUG-073/075).
    inline std::string MoveBlocker() {
        if (auto* ui = RE::UI::GetSingleton(); ui && ui->GameIsPaused()) {
            return "A menu is open (the map, inventory, a container, crafting or another pausing menu): close it and try again";
        }
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return "Player not available";
        if (player->IsOnMount()) return "She is on a mount: dismount first";
        if (auto* state = player->AsActorState(); state && state->GetSitSleepState() != RE::SIT_SLEEP_STATE::kNormal) {
            return "She is sitting, lying down or using furniture: get up first";
        }
        return {};
    }

    inline RE::FormID ParseFormId(const std::string& text) {
        std::size_t start = text.find_first_not_of(" \t");
        std::size_t end = text.find_last_not_of(" \t");
        if (start == std::string::npos) throw std::invalid_argument("empty FormID");
        std::string hex = text.substr(start, end - start + 1);
        if (hex.size() > 2 && hex[0] == '0' && (hex[1] == 'x' || hex[1] == 'X')) hex = hex.substr(2);
        if (hex.empty() || hex.size() > 8 ||
            !std::all_of(hex.begin(), hex.end(), [](unsigned char c) { return std::isxdigit(c) != 0; })) {
            throw std::invalid_argument("not a FormID: " + text);
        }
        return static_cast<RE::FormID>(std::stoul(hex, nullptr, 16));
    }

    inline std::optional<RE::ActorValue> ResolveActorValue(const std::string& attribute) {
        static const std::unordered_map<std::string, RE::ActorValue> map = {
            {"health", RE::ActorValue::kHealth}, {"magicka", RE::ActorValue::kMagicka},
            {"stamina", RE::ActorValue::kStamina}, {"carryweight", RE::ActorValue::kCarryWeight},
            {"speedmult", RE::ActorValue::kSpeedMult}, {"onehanded", RE::ActorValue::kOneHanded},
            {"twohanded", RE::ActorValue::kTwoHanded}, {"archery", RE::ActorValue::kArchery},
            {"block", RE::ActorValue::kBlock}, {"smithing", RE::ActorValue::kSmithing},
            {"heavyarmor", RE::ActorValue::kHeavyArmor}, {"lightarmor", RE::ActorValue::kLightArmor},
            {"pickpocket", RE::ActorValue::kPickpocket}, {"lockpicking", RE::ActorValue::kLockpicking},
            {"sneak", RE::ActorValue::kSneak}, {"alchemy", RE::ActorValue::kAlchemy},
            {"speech", RE::ActorValue::kSpeech}, {"alteration", RE::ActorValue::kAlteration},
            {"conjuration", RE::ActorValue::kConjuration}, {"destruction", RE::ActorValue::kDestruction},
            {"illusion", RE::ActorValue::kIllusion}, {"restoration", RE::ActorValue::kRestoration},
            {"enchanting", RE::ActorValue::kEnchanting},
        };
        auto it = map.find(attribute);
        if (it != map.end()) return it->second;
        return std::nullopt;
    }

    // ==================== Actor Values ====================

    // The maximum the game's own bars use: base value plus permanent modifiers (perks, gear,
    // racial bonuses) plus temporary ones (potions, spells). GetBaseActorValue alone gave
    // "magicka 150 of 100" on a Breton (BUG-015).
    inline float ActorValueMax(RE::Actor* actor, RE::ActorValue av) {
        return actor ? actor->GetActorValueMax(av) : 0.0f;
    }

    // ==================== Actor Resolution ====================

    inline RE::Actor* ResolveActor(const std::string& refId = "") {
        if (refId.empty() || refId == "player") {
            return RE::PlayerCharacter::GetSingleton();
        }
        auto* form = RE::TESForm::LookupByID(ParseFormId(refId));
        return form ? form->As<RE::Actor>() : nullptr;
    }

    // ==================== Name Resolution ====================

    inline std::string GetRefName(RE::TESObjectREFR* ref) {
        if (!ref) return "unknown";
        auto name = ref->GetName();
        return (name && name[0]) ? name : std::format("{:08X}", ref->GetFormID());
    }

    inline std::string GetFormName(RE::FormID formId) {
        auto* form = RE::TESForm::LookupByID(formId);
        if (!form) return std::format("{:08X}", formId);
        auto name = form->GetName();
        return (name && name[0]) ? name : std::format("{:08X}", formId);
    }

    // ==================== Spell / Magic Helpers ====================

    inline std::string ActorValueToSchool(RE::ActorValue av) {
        switch (av) {
            case RE::ActorValue::kAlteration: return "Alteration";
            case RE::ActorValue::kConjuration: return "Conjuration";
            case RE::ActorValue::kDestruction: return "Destruction";
            case RE::ActorValue::kIllusion: return "Illusion";
            case RE::ActorValue::kRestoration: return "Restoration";
            case RE::ActorValue::kEnchanting: return "Enchanting";
            default: return "Other";
        }
    }

    inline std::string SpellTypeToString(RE::MagicSystem::SpellType type) {
        switch (type) {
            case RE::MagicSystem::SpellType::kSpell: return "spell";
            case RE::MagicSystem::SpellType::kDisease: return "disease";
            case RE::MagicSystem::SpellType::kPower: return "power";
            case RE::MagicSystem::SpellType::kLesserPower: return "lesserPower";
            case RE::MagicSystem::SpellType::kAbility: return "ability";
            case RE::MagicSystem::SpellType::kPoison: return "poison";
            case RE::MagicSystem::SpellType::kAddiction: return "addiction";
            case RE::MagicSystem::SpellType::kVoicePower: return "shout";
            default: return "other";
        }
    }

    // ==================== Console Output Capture ====================
    //
    // Best-effort capture via lastMessage. Console output is not reliably
    // captured — commands execute silently via CompileAndRun without writing
    // to ConsoleLog. Use dedicated query tools instead of relying on console
    // output (GetPlayerInfo, GetSkillLevels, GetQuestStages, etc.).

    inline void ClearConsoleOutput() {
        auto* console = RE::ConsoleLog::GetSingleton();
        if (console) {
            std::memset(console->lastMessage, 0, sizeof(console->lastMessage));
        }
    }

    inline std::string CaptureConsoleOutput() {
        auto* console = RE::ConsoleLog::GetSingleton();
        if (!console) return "";
        return std::string(console->lastMessage);
    }

    // ==================== Game State Safety Check ====================
    //
    // Check if the game is in a safe state for executing commands.
    // Returns a JSON object with safety status and active risk flags.

    struct GameSafetyState {
        bool safe = true;
        bool isLoading = false;
        bool isPaused = false;
        bool isInCombat = false;
        bool isInKillMove = false;
        bool isInDialogue = false;
        bool isInScene = false;
        bool isMenuOpen = false;
        bool isSaving = false;
        std::string warning;
    };

    inline GameSafetyState CheckGameSafety() {
        GameSafetyState state;

        auto* ui = RE::UI::GetSingleton();
        if (ui) {
            state.isPaused = ui->GameIsPaused();
            state.isSaving = !ui->IsSavingAllowed();  // if saving not allowed, save is in progress
            state.isMenuOpen = ui->IsMenuOpen("Loading Menu");
            state.isLoading = state.isMenuOpen;

            // Check for dialogue
            if (ui->IsMenuOpen("Dialogue Menu")) {
                state.isInDialogue = true;
            }
        }

        auto* player = RE::PlayerCharacter::GetSingleton();
        if (player) {
            state.isInCombat = player->IsInCombat();
            state.isInKillMove = player->IsInKillMove();
        }

        // Determine overall safety
        if (state.isLoading) {
            state.safe = false;
            state.warning = "Game is loading — commands will crash";
        } else if (state.isInKillMove) {
            state.safe = false;
            state.warning = "Player is in a kill move — commands may crash";
        } else if (state.isSaving) {
            state.safe = false;
            state.warning = "Game is saving — commands may corrupt save";
        }

        return state;
    }

    inline json GetGameSafetyJson() {
        auto state = CheckGameSafety();
        json result;
        result["safe"] = state.safe;
        result["isLoading"] = state.isLoading;
        result["isPaused"] = state.isPaused;
        result["isInCombat"] = state.isInCombat;
        result["isInKillMove"] = state.isInKillMove;
        result["isInDialogue"] = state.isInDialogue;
        result["isSaving"] = state.isSaving;
        if (!state.warning.empty()) result["warning"] = state.warning;
        return result;
    }

    // ==================== Console Command Execution ====================
    //
    // Console command execution approach adapted from ConsoleUtilSSE by VersuchDrei
    // (https://github.com/VersuchDrei/ConsoleUtilSSE, MIT License)
    //
    // Key insight: the function moved to a new Address Library ID in 1.6.1130, so the
    // relocation has to be chosen by runtime version (441582 from 1.6.1130 onward).
    //
    // The whole version is compared, not the patch number alone: Skyrim 1.7.104 has
    // patch 104, which a "patch < 1130" test would send to the pre-1130 ID.

    inline void CompileAndRunImpl(RE::Script* script, RE::ScriptCompiler* compiler,
        RE::COMPILER_NAME name, RE::TESObjectREFR* targetRef) {
        using func_t = decltype(CompileAndRunImpl);
        REL::Relocation<func_t> func{
            RELOCATION_ID(21416, AE_CHECK(SKSE::RUNTIME_SSE_1_6_1130, 21890, 441582))
        };
        return func(script, compiler, name, targetRef);
    }

    inline void CompileAndRunSafe(RE::Script* script, RE::TESObjectREFR* targetRef,
        RE::COMPILER_NAME name = RE::COMPILER_NAME::kSystemWindowCompiler) {
        RE::ScriptCompiler compiler;
        CompileAndRunImpl(script, &compiler, name, targetRef);
    }

    // SEH wrapper for safety
    inline bool RunScriptSEH(RE::Script* script, RE::TESObjectREFR* targetRef) {
        __try {
            CompileAndRunSafe(script, targetRef);
            return true;
        } __except(EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    // Runs a console command as if `targetRef` were selected in the console. Null means
    // nothing selected, like typing the command with no reference clicked: some
    // commands act differently then (`tcl` with nothing selected is the player's
    // no-clip; with the player selected it only sets the player's reference flag).
    inline json ExecuteConsoleCommandOn(const std::string& command, RE::TESObjectREFR* targetRef) {
        // Console command execution using the approach from ConsoleUtilSSE
        // by VersuchDrei (https://github.com/VersuchDrei/ConsoleUtilSSE, MIT License)

        // Log safety state but don't block — individual functions handle their own fallbacks
        auto safety = CheckGameSafety();
        if (!safety.safe) {
            SKSE::log::warn("Command '{}' executing in unsafe state: {}", command, safety.warning);
        }

        auto* factory = RE::IFormFactory::GetConcreteFormFactoryByType<RE::Script>();
        if (!factory) return {{"error", "Script factory not available"}};

        auto* script = factory->Create();
        if (!script) return {{"error", "Failed to create script"}};

        ClearConsoleOutput();

        script->SetCommand(command);
        bool ok = RunScriptSEH(script, targetRef);

        std::string output = CaptureConsoleOutput();

        if (!ok) {
            SKSE::log::error("Command '{}' caused an access violation — caught by SEH", command);
            return {{"error", "Command failed (caught safely): " + command},
                    {"command", command},
                    {"partialOutput", output}};
        }

        SKSE::log::info("Command '{}' executed, output: '{}' ({} bytes)",
            command, output.substr(0, 100), output.size());

        json result;
        result["executed"] = true;
        result["command"] = command;
        if (!output.empty()) result["output"] = output;
        return result;
    }

    // The execute_command tool's behaviour: the player is the selected reference, so
    // `additem f 100` gives the player gold without a `player.` prefix.
    inline json ExecuteConsoleCommand(const std::string& command) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};
        return ExecuteConsoleCommandOn(command, player);
    }

}

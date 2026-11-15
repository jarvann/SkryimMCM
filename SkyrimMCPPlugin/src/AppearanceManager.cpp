#include "AppearanceManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <cctype>
#include <climits>
#include <cstdio>
#include <format>
#include <optional>
#include <string_view>
#include <vector>

namespace SkyrimMCP::AppearanceManager {

    using json = nlohmann::json;

    namespace {
        struct Rgb {
            int r = 0, g = 0, b = 0;
        };

        std::string Hex(const Rgb& c) { return std::format("#{:02X}{:02X}{:02X}", c.r, c.g, c.b); }
        Rgb FromColor(const RE::Color& c) { return {c.red, c.green, c.blue}; }

        std::string Lower(std::string_view text) {
            std::string out(text);
            std::transform(out.begin(), out.end(), out.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
            return out;
        }

        // "#RRGGBB", "RRGGBB" or "r,g,b" (0-255 each).
        std::optional<Rgb> ParseRgb(const std::string& text) {
            std::string s = text;
            s.erase(std::remove_if(s.begin(), s.end(), [](unsigned char ch) { return std::isspace(ch); }), s.end());
            if (s.find(',') != std::string::npos) {
                Rgb c;
                if (std::sscanf(s.c_str(), "%d,%d,%d", &c.r, &c.g, &c.b) != 3) return std::nullopt;
                if (c.r < 0 || c.r > 255 || c.g < 0 || c.g > 255 || c.b < 0 || c.b > 255) return std::nullopt;
                return c;
            }
            if (!s.empty() && s[0] == '#') s.erase(0, 1);
            if (s.size() != 6 || !std::all_of(s.begin(), s.end(), [](unsigned char ch) { return std::isxdigit(ch); })) return std::nullopt;
            const auto v = std::stoul(s, nullptr, 16);
            return Rgb{static_cast<int>((v >> 16) & 0xFF), static_cast<int>((v >> 8) & 0xFF), static_cast<int>(v & 0xFF)};
        }

        // Plain colour words, mapped to typical values so "dark brown" or "platinum" work
        // without the AI knowing the load order's colour forms.
        std::optional<Rgb> NamedColor(const std::string& text) {
            static const std::pair<const char*, Rgb> names[] = {
                {"black", {20, 18, 17}}, {"dark brown", {48, 33, 25}}, {"brown", {82, 56, 40}},
                {"light brown", {125, 92, 64}}, {"auburn", {120, 50, 30}}, {"red", {145, 45, 25}},
                {"ginger", {175, 85, 40}}, {"copper", {160, 90, 50}}, {"strawberry blonde", {200, 140, 100}},
                {"dark blonde", {150, 120, 80}}, {"blonde", {210, 175, 120}}, {"platinum", {230, 225, 205}},
                {"grey", {140, 140, 140}}, {"gray", {140, 140, 140}}, {"silver", {190, 190, 195}},
                {"white", {235, 235, 235}}, {"blue", {40, 70, 160}}, {"green", {40, 120, 60}},
                {"purple", {100, 50, 130}}, {"pink", {220, 120, 160}},
                // skin
                {"pale", {245, 225, 210}}, {"fair", {235, 205, 180}}, {"tan", {200, 155, 115}},
                {"olive", {180, 145, 100}}, {"dark", {110, 75, 55}}, {"ebony", {70, 48, 38}},
            };
            const auto wanted = Lower(text);
            for (const auto& [name, rgb] : names) {
                if (wanted == name) return rgb;
            }
            return std::nullopt;
        }

        int DistanceSq(const Rgb& a, const Rgb& b) {
            const int dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            // Weighted toward green like the eye, so "closest" looks closest.
            return 2 * dr * dr + 4 * dg * dg + 3 * db * db;
        }

        json DescribeColorForm(RE::BGSColorForm* form) {
            json c;
            c["formId"] = std::format("{:08X}", form->GetFormID());
            const char* name = form->GetName();
            const char* editorId = form->GetFormEditorID();
            if (name && name[0]) c["name"] = name;
            if (editorId && editorId[0]) c["editorId"] = editorId;
            c["rgb"] = Hex(FromColor(form->color));
            c["playable"] = form->flags.all(RE::BGSColorForm::Flag::kPlayable);
            if (auto* file = form->GetFile(0)) c["mod"] = file->fileName;
            return c;
        }

        // The player's face is drawn from these tint layers; the skin-tone layer is the
        // one the character creator's "Skin tone" slider sets.
        std::vector<RE::TintMask*> PlayerSkinToneMasks(RE::PlayerCharacter* player) {
            std::vector<RE::TintMask*> masks;
            for (auto* mask : player->GetPlayerRuntimeData().tintMasks) {
                if (mask && mask->type.all(RE::TintMask::Type::kSkinTone)) masks.push_back(mask);
            }
            return masks;
        }

        std::string ActorLabel(RE::Actor* actor) {
            return actor->IsPlayerRef() ? std::string("player") : std::format("{:08X}", actor->GetFormID());
        }
    }

    json GetColors(const std::string& refId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* base = actor->GetActorBase();
        if (!base) return {{"error", "Actor has no base record"}};

        json result;
        result["actor"] = ActorLabel(actor);
        if (base->headRelatedData && base->headRelatedData->hairColor) {
            result["hairColor"] = DescribeColorForm(base->headRelatedData->hairColor);
        } else {
            result["hairColor"] = nullptr;
        }
        result["skinTone"] = Hex(FromColor(base->bodyTintColor));

        if (actor->IsPlayerRef()) {
            json masks = json::array();
            for (auto* mask : PlayerSkinToneMasks(static_cast<RE::PlayerCharacter*>(actor))) {
                masks.push_back({{"rgb", Hex(FromColor(mask->color))}, {"alpha", mask->alpha}});
            }
            result["skinToneTintMasks"] = masks;
        }
        return result;
    }

    json ListHairColors(bool includeNonPlayable) {
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};

        json colors = json::array();
        for (auto* form : dataHandler->GetFormArray<RE::BGSColorForm>()) {
            if (!form) continue;
            if (!includeNonPlayable && !form->flags.all(RE::BGSColorForm::Flag::kPlayable)) continue;
            colors.push_back(DescribeColorForm(form));
        }
        return {{"colors", colors}, {"count", colors.size()}};
    }

    json SetHairColor(const std::string& refId, const std::string& color) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* base = actor->GetActorBase();
        if (!base) return {{"error", "Actor has no base record"}};
        if (color.empty()) return {{"error", "Missing color"}};

        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};
        auto& forms = dataHandler->GetFormArray<RE::BGSColorForm>();

        RE::BGSColorForm* chosen = nullptr;
        std::string matchedBy;

        // 1. A colour form ID.
        try {
            if (auto* form = RE::TESForm::LookupByID<RE::BGSColorForm>(Helpers::ParseFormId(color))) {
                chosen = form;
                matchedBy = "formId";
            }
        } catch (...) {}

        // 2. A colour form's name or editor ID (exact, then contains).
        if (!chosen && !ParseRgb(color) && !NamedColor(color)) {
            const auto wanted = Lower(color);
            RE::BGSColorForm* partial = nullptr;
            for (auto* form : forms) {
                if (!form) continue;
                const char* name = form->GetName();
                const char* editorId = form->GetFormEditorID();
                const auto lname = Lower(name ? name : "");
                const auto leditor = Lower(editorId ? editorId : "");
                if (lname == wanted || leditor == wanted) { chosen = form; break; }
                if (!partial && ((!lname.empty() && lname.find(wanted) != std::string::npos) ||
                                 (!leditor.empty() && leditor.find(wanted) != std::string::npos))) {
                    partial = form;
                }
            }
            if (!chosen) chosen = partial;
            if (chosen) matchedBy = "name";
        }

        // 3. An RGB value or colour word: the closest playable hair colour form. Using an
        //    existing form keeps the change in the save; a form made at runtime would not load.
        std::optional<Rgb> wantedRgb;
        int distance = 0;
        if (!chosen) {
            wantedRgb = ParseRgb(color);
            if (!wantedRgb) wantedRgb = NamedColor(color);
            if (!wantedRgb) {
                return {{"error", "Unknown colour '" + color + "'. Use a hair colour form ID or name (ListHairColors), "
                                  "#RRGGBB, r,g,b, or a colour word such as black, brown, auburn, red, blonde, grey or white."}};
            }
            int best = INT_MAX;
            for (int pass = 0; pass < 2 && !chosen; ++pass) {  // playable forms first, then any
                for (auto* form : forms) {
                    if (!form) continue;
                    if (pass == 0 && !form->flags.all(RE::BGSColorForm::Flag::kPlayable)) continue;
                    const int d = DistanceSq(FromColor(form->color), *wantedRgb);
                    if (d < best) { best = d; chosen = form; }
                }
            }
            if (!chosen) return {{"error", "No hair colour forms are loaded"}};
            distance = best;
            matchedBy = "closestRgb";
        }

        json previous = (base->headRelatedData && base->headRelatedData->hairColor)
                            ? DescribeColorForm(base->headRelatedData->hairColor) : json(nullptr);

        base->SetHairColor(chosen);
        base->AddChange(RE::TESNPC::ChangeFlags::kFace);  // saved with the actor's face data
        actor->UpdateHairColor();

        json result;
        result["actor"] = ActorLabel(actor);
        result["previous"] = previous;
        result["hairColor"] = DescribeColorForm(chosen);
        result["matchedBy"] = matchedBy;
        if (wantedRgb) {
            result["requested"] = Hex(*wantedRgb);
            result["note"] = distance == 0 ? "Exact match."
                : "The closest existing hair colour was used, so the change stays after saving and loading. "
                  "ListHairColors shows every option.";
        }
        if (!actor->IsPlayerRef()) {
            result["npcNote"] = "NPC hair usually follows at once; if not, it updates when the NPC's 3D reloads (leave and re-enter the cell).";
        }
        return result;
    }

    json SetSkinTone(const std::string& refId, const std::string& color, bool refresh) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* base = actor->GetActorBase();
        if (!base) return {{"error", "Actor has no base record"}};

        auto rgb = ParseRgb(color);
        if (!rgb) rgb = NamedColor(color);
        if (!rgb) {
            return {{"error", "Unknown colour '" + color + "'. Use #RRGGBB, r,g,b, or one of pale, fair, tan, olive, dark, ebony."}};
        }

        const std::string previous = Hex(FromColor(base->bodyTintColor));
        base->bodyTintColor.red = static_cast<std::uint8_t>(rgb->r);
        base->bodyTintColor.green = static_cast<std::uint8_t>(rgb->g);
        base->bodyTintColor.blue = static_cast<std::uint8_t>(rgb->b);
        base->AddChange(RE::TESNPC::ChangeFlags::kFace);

        int masksChanged = 0;
        if (actor->IsPlayerRef()) {
            // The game rebuilds the player's face from these layers when the game loads,
            // so they carry the change into the save.
            for (auto* mask : PlayerSkinToneMasks(static_cast<RE::PlayerCharacter*>(actor))) {
                mask->color.red = static_cast<std::uint8_t>(rgb->r);
                mask->color.green = static_cast<std::uint8_t>(rgb->g);
                mask->color.blue = static_cast<std::uint8_t>(rgb->b);
                ++masksChanged;
            }
        }

        actor->UpdateSkinColor();  // body, hands and feet, first and third person
        if (refresh) actor->DoReset3D(false);

        json result;
        result["actor"] = ActorLabel(actor);
        result["previous"] = previous;
        result["skinTone"] = Hex(*rgb);
        result["refreshed3D"] = refresh;
        if (actor->IsPlayerRef()) {
            result["tintMasksChanged"] = masksChanged;
            result["note"] = refresh
                ? "Body updated now and the 3D was rebuilt. If the face still shows the old tone, save and load: the game rebuilds the face from its tint layers on load."
                : "Body updated now. The face may keep the old tone until the 3D is rebuilt (call again with refresh=true) or the game is saved and loaded.";
        } else {
            result["note"] = "NPC faces use pre-baked textures, so the face may not match the new body tone.";
        }
        return result;
    }

}

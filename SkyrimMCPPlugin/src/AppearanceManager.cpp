#include "AppearanceManager.h"
#include "Helpers.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cctype>
#include <climits>
#include <cstdio>
#include <format>
#include <iterator>
#include <utility>
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

        // BUG-037: hair colours are matched in Lab with lightness weighted down, so a dark auburn finds a dark red
        // rather than a plain brown of the same brightness (plain RGB distance lost the red).
        std::array<double, 3> ToLab(const Rgb& c) {
            auto lin = [](int v) { const double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : std::pow((x + 0.055) / 1.055, 2.4); };
            const double r = lin(c.r), g = lin(c.g), b = lin(c.b);
            const double X = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
            const double Y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
            const double Z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
            auto f = [](double t) { constexpr double e = 6.0 / 29; return t > e * e * e ? std::cbrt(t) : t / (3 * e * e) + 4.0 / 29; };
            return { 116 * f(Y) - 16, 500 * (f(X) - f(Y)), 200 * (f(Y) - f(Z)) };
        }

        double HairDistance(const Rgb& a, const Rgb& b) {
            const auto la = ToLab(a), lb = ToLab(b);
            const double dl = (la[0] - lb[0]) * 0.5, da = la[1] - lb[1], db = la[2] - lb[2];
            return dl * dl + da * da + db * db;
        }

        json DescribeColorForm(RE::BGSColorForm* form) {
            json c;
            c["formId"] = std::format("{:08X}", form->GetFormID());
            const char* name = form->GetName();
            const char* editorId = form->GetFormEditorID();
            if (name && name[0]) c["name"] = name;
            if (editorId && editorId[0]) c["editorId"] = editorId;
            c["rgb"] = Hex(FromColor(form->color));
            // BUG-035: hair and brows are drawn at twice the stored colour (seen in game: #5E442E drawn #BC885C).
            const auto stored = FromColor(form->color);
            c["onScreen"] = Hex({std::min(255, stored.r * 2), std::min(255, stored.g * 2), std::min(255, stored.b * 2)});
            c["playable"] = form->flags.all(RE::BGSColorForm::Flag::kPlayable);
            if (auto* file = form->GetFile(0)) c["mod"] = file->fileName;
            return c;
        }

        // The player's face is drawn from these tint layers; the skin-tone layer is the
        // one the character creator's "Skin tone" slider sets.
        std::vector<RE::TintMask*> PlayerSkinToneMasks(RE::PlayerCharacter* player) {
            std::vector<RE::TintMask*> masks;
            for (auto* mask : player->GetPlayerRuntimeData().tintMasks) {
                if (mask && mask->type.get() == RE::TintMask::Type::kSkinTone) masks.push_back(mask);  // a value, not a bit flag
            }
            return masks;
        }

        std::string ActorLabel(RE::Actor* actor) {
            return actor->IsPlayerRef() ? std::string("player") : std::format("{:08X}", actor->GetFormID());
        }
    }

    namespace {
        std::string LowerFileName(std::string_view path) {
            auto slash = path.find_last_of("\\/");
            std::string name(slash == std::string_view::npos ? path : path.substr(slash + 1));
            std::transform(name.begin(), name.end(), name.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            return name;
        }

        // "MaleHeadNord_Lips.dds" and "FemaleHeadNord_Lips.dds" both become "headnord_lips.dds".
        std::string SexlessName(std::string_view path) {
            auto name = LowerFileName(path);
            for (std::string_view word : { std::string_view("female"), std::string_view("male") }) {
                for (auto at = name.find(word); at != std::string::npos; at = name.find(word)) name.erase(at, word.size());
            }
            return name;
        }

        using TintAsset = RE::TESRace::FaceRelatedData::TintAsset;

        const TintAsset* FindAsset(RE::BSTArray<TintAsset*>* list, const RE::TintMask* mask) {
            if (!list || !mask->texture) return nullptr;
            const auto file = LowerFileName(mask->texture->textureName.c_str());
            for (auto* asset : *list) {
                if (!asset) continue;
                if (&asset->texture.file == mask->texture) return asset;
                if (LowerFileName(asset->texture.file.textureName.c_str()) == file) return asset;
            }
            return nullptr;
        }

        const TintAsset* MatchInOwnSet(RE::BSTArray<TintAsset*>* own, const TintAsset* other, const RE::TintMask* mask) {
            const auto key = SexlessName(mask->texture->textureName.c_str());
            for (auto* asset : *own) {
                if (asset && SexlessName(asset->texture.file.textureName.c_str()) == key) return asset;
            }
            // Different file names: the only layer of the same kind (lips, cheeks...), never paint or dirt, which come in many.
            if (!other) return nullptr;
            const auto kind = other->texture.skinTone.get();
            using K = TintAsset::TintLayer::SkinTone;
            if (kind == K::kPaint || kind == K::kDirt || kind == K::kNone) return nullptr;
            const TintAsset* only = nullptr;
            for (auto* asset : *own) {
                if (!asset || asset->texture.skinTone.get() != kind) continue;
                if (only) return nullptr;
                only = asset;
            }
            return only;
        }
    }

    json PlayerTintSex(bool fix) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* base = player ? player->GetActorBase() : nullptr;
        auto* race = player ? player->GetRace() : nullptr;
        if (!base || !race) return {{"error", "Player not loaded"}};
        const bool female = base->GetSex() == RE::SEX::kFemale;
        auto* ownData = race->faceRelatedData[female ? RE::SEX::kFemale : RE::SEX::kMale];
        auto* otherData = race->faceRelatedData[female ? RE::SEX::kMale : RE::SEX::kFemale];
        auto* own = ownData ? ownData->tintMasks : nullptr;
        auto* other = otherData ? otherData->tintMasks : nullptr;
        if (!own || own->empty()) return {{"error", std::string("The player's race has no ") + (female ? "female" : "male") + " makeup layers"}};

        int layers = 0, right = 0, wrongSex = 0, fixed = 0;
        json unmatched = json::array();
        json changes = json::array();
        auto visit = [&](RE::BSTArray<RE::TintMask*>& masks) {
            for (auto* mask : masks) {
                if (!mask || !mask->texture) continue;
                ++layers;
                if (FindAsset(own, mask)) { ++right; continue; }
                const auto* otherAsset = FindAsset(other, mask);
                if (!otherAsset) continue;  // from neither set (another mod's layer): left alone
                ++wrongSex;
                const std::string was = mask->texture->textureName.c_str();
                const auto* match = MatchInOwnSet(own, otherAsset, mask);
                if (!match) {
                    unmatched.push_back({{"texture", was}, {"visible", mask->alpha > 0.0f}});
                    continue;
                }
                if (fix) {
                    if (mask->texture == &otherAsset->texture.file) {
                        // Points into the race record: point it at the right one instead (never edit the race's own data).
                        mask->texture = const_cast<RE::TESTexture*>(&match->texture.file);
                    } else {
                        mask->texture->textureName = match->texture.file.textureName;  // the player's own copy
                    }
                    ++fixed;
                }
                if (changes.size() < 40) changes.push_back({{"from", LowerFileName(was)}, {"to", LowerFileName(match->texture.file.textureName.c_str())},
                                                            {"visible", mask->alpha > 0.0f}});
            }
        };
        visit(player->GetPlayerRuntimeData().tintMasks);
        if (auto* overlay = player->GetPlayerRuntimeData().overlayTintMasks) visit(*overlay);
        if (fixed > 0) base->AddChange(RE::TESNPC::ChangeFlags::kFace);

        json result;
        result["sex"] = female ? "female" : "male";
        result["layers"] = layers;
        result["ownSet"] = right;
        result["otherSexSet"] = wrongSex;
        result["fixed"] = fixed;
        result["changes"] = changes;
        if (!unmatched.empty()) result["unmatched"] = unmatched;
        return result;
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

    json SetHairColor(const std::string& refId, const std::string& color, bool exact) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* base = actor->GetActorBase();
        if (!base) return {{"error", "Actor has no base record"}};
        if (color.empty()) return {{"error", "Missing color"}};

        // FEAT-247: the exact colour, the way RaceMenu does it: RaceMenu has a hair colour form of its own, which it
        // saves and restores with the character, so its colour can be anything. Only for the player.
        if (exact) {
            auto wanted = ParseRgb(color);
            if (!wanted) wanted = NamedColor(color);
            if (!wanted) return {{"error", "exact needs #RRGGBB, r,g,b or a colour word"}};
            if (!actor->IsPlayerRef()) return {{"error", "exact works for the player only (it uses RaceMenu's own hair colour)"}};
            auto* dataHandler = RE::TESDataHandler::GetSingleton();
            if (!dataHandler) return {{"error", "Data handler not available"}};
            RE::BGSColorForm* raceMenuForm = nullptr;
            for (auto* form : dataHandler->GetFormArray<RE::BGSColorForm>()) {
                auto* file = form ? form->GetFile(0) : nullptr;
                if (file && Lower(file->fileName).find("racemenu") != std::string::npos) { raceMenuForm = form; break; }
            }
            if (!raceMenuForm) return {{"error", "RaceMenu's hair colour form wasn't found (is RaceMenu installed?): use SetHairColor without exact"}};
            json previous = (base->headRelatedData && base->headRelatedData->hairColor)
                                ? DescribeColorForm(base->headRelatedData->hairColor) : json(nullptr);
            // The game draws hair at twice the stored colour, so store half of what should show.
            raceMenuForm->color.red = static_cast<std::uint8_t>(wanted->r / 2);
            raceMenuForm->color.green = static_cast<std::uint8_t>(wanted->g / 2);
            raceMenuForm->color.blue = static_cast<std::uint8_t>(wanted->b / 2);
            base->SetHairColor(raceMenuForm);
            base->AddChange(RE::TESNPC::ChangeFlags::kFace);
            actor->UpdateHairColor();
            return {{"actor", ActorLabel(actor)},
                    {"previous", previous},
                    {"hairColor", DescribeColorForm(raceMenuForm)},
                    {"requested", Hex(*wanted) + " on screen"},
                    {"matchedBy", "exact (RaceMenu's own hair colour form)"},
                    {"note", "Exact colour in RaceMenu's hair colour form, which RaceMenu keeps with the character. Check it after a save and load."}};
        }

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
        std::string matchQuality;
        json alternatives = json::array();
        if (!chosen) {
            wantedRgb = ParseRgb(color);
            if (!wantedRgb) wantedRgb = NamedColor(color);
            if (!wantedRgb) {
                return {{"error", "Unknown colour '" + color + "'. Use a hair colour form ID or name (ListHairColors), "
                                  "#RRGGBB, r,g,b, or a colour word such as black, brown, auburn, red, blonde, grey or white."}};
            }
            // The requested colour is what should show on screen; the game draws hair at twice the stored
            // colour, so look for a form near half of it (BUG-035: "auburn" #7E2E2E came out light copper-pink).
            const Rgb storedTarget{ wantedRgb->r / 2, wantedRgb->g / 2, wantedRgb->b / 2 };
            std::vector<std::pair<double, RE::BGSColorForm*>> ranked;
            for (int pass = 0; pass < 2 && ranked.empty(); ++pass) {  // playable forms first, then any
                for (auto* form : forms) {
                    if (!form) continue;
                    if (pass == 0 && !form->flags.all(RE::BGSColorForm::Flag::kPlayable)) continue;
                    // BUG-058: colour forms for lips, skin, eyes and paint are playable too in some load orders; they
                    // aren't hair colours (LipsRedguardMale03 came up as an alternative for strawberry blonde).
                    if (const char* ed = form->GetFormEditorID(); ed && ed[0]) {
                        const auto low = Lower(ed);
                        if (low.find("lip") != std::string::npos || low.find("skin") != std::string::npos ||
                            low.find("eye") != std::string::npos || low.find("paint") != std::string::npos ||
                            low.find("dirt") != std::string::npos) continue;
                    }
                    ranked.emplace_back(HairDistance(FromColor(form->color), storedTarget), form);
                }
            }
            if (ranked.empty()) return {{"error", "No hair colour forms are loaded"}};
            std::sort(ranked.begin(), ranked.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
            chosen = ranked[0].second;
            distance = static_cast<int>(ranked[0].first);
            // How close the best match is (Lab difference, lightness half-weighted), and the next options, since
            // a load order often has nothing near a given shade.
            const double gap = std::sqrt(ranked[0].first);
            matchQuality = gap < 3 ? "close" : gap < 8 ? "approximate" : "far: no hair colour in the load order is near this shade";
            for (std::size_t i = 1; i < ranked.size() && i < 4; i++) alternatives.push_back(DescribeColorForm(ranked[i].second));
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
            result["requested"] = Hex(*wantedRgb) + " on screen";
            result["match"] = matchQuality;
            if (!alternatives.empty()) result["alternatives"] = alternatives;
            result["note"] = std::string(distance == 0 ? "Exact match. " : "The closest existing hair colour was used, so the change stays after saving and loading. ") +
                "Hair is drawn at twice the stored colour (onScreen), so the stored value is about half the request. ListHairColors shows every option.";
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

    namespace {
        std::string EditorIdOf(RE::BGSHeadPart* part) {
            const char* id = part->GetFormEditorID();
            return (id && id[0]) ? id : std::format("{:08X}", part->GetFormID());
        }

        int HeadPartTypeFromName(const std::string& name) {
            static const char* names[] = { "misc", "face", "eyes", "hair", "facialhair", "scar", "eyebrows" };
            const auto wanted = Lower(name);
            for (int i = 0; i < static_cast<int>(std::size(names)); ++i) {
                if (wanted == names[i]) return i;
            }
            if (wanted == "brows") return 6;
            if (wanted == "beard") return 4;
            return -1;
        }
    }

    namespace {
        bool SexMatches(RE::BGSHeadPart* part, bool female) {
            const bool male = part->flags.all(RE::BGSHeadPart::Flag::kMale);
            const bool fem = part->flags.all(RE::BGSHeadPart::Flag::kFemale);
            if (!male && !fem) return true;
            return female ? fem : male;
        }
    }

    std::vector<RE::BGSHeadPart*> HeadPartChoices(RE::TESNPC* npc, int type, const std::string& order) {
        std::vector<RE::BGSHeadPart*> list;
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!npc || !dataHandler) return list;
        auto* race = npc->GetRace();
        const bool female = npc->GetSex() == RE::SEX::kFemale;
        for (auto* part : dataHandler->GetFormArray<RE::BGSHeadPart>()) {
            if (!part || part->IsExtraPart()) continue;
            if (!part->flags.all(RE::BGSHeadPart::Flag::kPlayable)) continue;
            if (static_cast<int>(std::to_underlying(part->type.get())) != type) continue;
            // A part with neither sex flag is offered to both, as RaceMenu does (found in game: "NoEyes"
            // and "NoHair" parts from boss mods were the entries RaceMenu showed and this list didn't).
            if (!SexMatches(part, female)) continue;
            if (race && part->validRaces && !part->validRaces->HasForm(race)) continue;
            list.push_back(part);
        }
        if (order == "loadOrder") return list;  // the data handler's order
        std::vector<std::pair<std::string, RE::BGSHeadPart*>> keyed;
        keyed.reserve(list.size());
        for (auto* part : list) {
            std::string key;
            if (order == "name") {
                const char* name = part->GetName();
                key = (name && name[0]) ? name : EditorIdOf(part);
            } else {
                key = EditorIdOf(part);
            }
            if (order == "editorIdNoCase" || order == "name") key = Lower(key);
            keyed.emplace_back(std::move(key), part);
        }
        std::stable_sort(keyed.begin(), keyed.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
        for (std::size_t i = 0; i < keyed.size(); ++i) list[i] = keyed[i].second;
        return list;
    }

    json ListHeadPartChoices(const std::string& refId, const std::string& type, const std::string& order,
                             int start, int count, const std::string& aroundFormId) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* npc = actor->GetActorBase();
        const int t = HeadPartTypeFromName(type);
        if (t < 0) return {{"error", "type must be hair, eyes, eyebrows, facialHair, scar, face or misc"}};
        const std::string ord = order.empty() ? "editorId" : order;
        if (std::find_if(std::begin(kHeadPartOrders), std::end(kHeadPartOrders), [&](const char* o) { return ord == o; }) == std::end(kHeadPartOrders))
            return {{"error", "order must be editorId, editorIdNoCase, name or loadOrder"}};

        auto list = HeadPartChoices(npc, t, ord);
        count = std::clamp(count, 1, 2000);
        if (!aroundFormId.empty()) {
            RE::FormID wanted = 0;
            try { wanted = Helpers::ParseFormId(aroundFormId); } catch (...) { return {{"error", "Invalid aroundFormId"}}; }
            auto it = std::find_if(list.begin(), list.end(), [&](RE::BGSHeadPart* p) { return p->GetFormID() == wanted; });
            if (it == list.end()) return {{"error", "That head part isn't in this list (wrong type, sex or race, or not playable)"}};
            start = static_cast<int>(it - list.begin()) - count / 2;
        }
        start = std::clamp(start, 0, std::max(0, static_cast<int>(list.size()) - 1));

        json entries = json::array();
        for (int i = start; i < static_cast<int>(list.size()) && i < start + count; ++i) {
            auto* part = list[i];
            json e;
            e["index"] = i;
            e["formId"] = std::format("{:08X}", part->GetFormID());
            e["editorId"] = EditorIdOf(part);
            if (const char* name = part->GetName(); name && name[0]) e["name"] = name;
            if (auto* file = part->GetFile(0)) e["mod"] = file->fileName;
            // FEAT-236: the mesh, so the hair catalogue can read its shape from disk.
            if (const char* model = part->GetModel(); model && model[0]) e["model"] = model;
            entries.push_back(std::move(e));
        }
        return {{"type", type}, {"order", ord}, {"total", list.size()}, {"start", start}, {"entries", entries}};
    }

    namespace {
        const char* kTypeNames[] = { "misc", "face", "eyes", "hair", "facialHair", "scar", "eyebrows" };

        const char* TypeName(RE::BGSHeadPart* part) {
            const auto t = static_cast<int>(std::to_underlying(part->type.get()));
            return (t >= 0 && t < static_cast<int>(std::size(kTypeNames))) ? kTypeNames[t] : "other";
        }

        json DescribePart(RE::BGSHeadPart* part) {
            if (!part) return nullptr;
            json p;
            p["formId"] = std::format("{:08X}", part->GetFormID());
            p["editorId"] = EditorIdOf(part);
            p["type"] = TypeName(part);
            if (const char* name = part->GetName(); name && name[0]) p["name"] = name;
            if (auto* file = part->GetFile(0)) p["mod"] = file->fileName;
            return p;
        }

        // A head part by FormID or editor ID, or by type + index in the character menu's order
        // (ListHeadPartChoices, the same numbers RaceMenu shows).
        RE::BGSHeadPart* FindPart(RE::TESNPC* npc, const std::string& part, const std::string& type, int index, std::string& error) {
            if (!part.empty()) {
                RE::TESForm* form = nullptr;
                try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(part)); } catch (...) {}
                if (!form) form = RE::TESForm::LookupByEditorID(part);
                auto* hp = form ? form->As<RE::BGSHeadPart>() : nullptr;
                if (!hp) error = "No head part with FormID or editor ID '" + part + "'";
                return hp;
            }
            const int t = HeadPartTypeFromName(type);
            if (t < 0) { error = "Pass part (FormID or editor ID), or type and index from ListHeadPartChoices"; return nullptr; }
            auto list = HeadPartChoices(npc, t, "editorId");
            if (index < 0 || index >= static_cast<int>(list.size())) {
                error = std::format("index must be 0 to {} for {}", static_cast<int>(list.size()) - 1, type);
                return nullptr;
            }
            return list[index];
        }

        json CurrentParts(RE::TESNPC* npc) {
            json parts = json::array();
            if (npc->headParts) {
                for (std::int8_t i = 0; i < npc->numHeadParts; ++i) {
                    if (npc->headParts[i]) parts.push_back(DescribePart(npc->headParts[i]));
                }
            }
            return parts;
        }
    }

    json SetHeadPart(const std::string& refId, const std::string& part, const std::string& type, int index, bool refresh) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* npc = actor->GetActorBase();
        if (!npc) return {{"error", "Actor has no base record"}};
        if (npc->HasOverlays()) return {{"error", "This character uses head part overlays; changing them isn't supported"}};

        std::string error;
        auto* newPart = FindPart(npc, part, type, index, error);
        if (!newPart) return {{"error", error}};
        if (newPart->IsExtraPart()) return {{"error", EditorIdOf(newPart) + " is an extra part (added with its main part); pick the main part instead"}};
        const auto newType = newPart->type.get();
        if (newType == RE::BGSHeadPart::HeadPartType::kFace) {
            return {{"error", "Changing the face part isn't supported: it carries the face shape and would distort the head"}};
        }

        // Misc parts stack (several can be worn at once), so a new one is added; every other
        // type replaces the current part of that type.
        RE::BGSHeadPart* oldPart = newType == RE::BGSHeadPart::HeadPartType::kMisc ? nullptr : npc->GetCurrentHeadPartByType(newType);
        if (oldPart == newPart) return {{"actor", ActorLabel(actor)}, {"unchanged", true}, {"part", DescribePart(newPart)}};

        json warnings = json::array();
        auto choices = HeadPartChoices(npc, static_cast<int>(std::to_underlying(newType)), "editorId");
        auto it = std::find(choices.begin(), choices.end(), newPart);
        if (it == choices.end()) warnings.push_back("This part isn't in the character menu's list for this race and sex (not playable, other sex or race); it may not fit.");

        npc->ChangeHeadPart(newPart);           // the record (saved with the game)
        actor->ChangeHeadPart(oldPart, newPart);  // the loaded 3D
        npc->AddChange(RE::TESNPC::ChangeFlags::kFace);
        if (refresh) actor->DoReset3D(false);

        json result;
        result["actor"] = ActorLabel(actor);
        result["previous"] = DescribePart(oldPart);
        result["part"] = DescribePart(newPart);
        if (it != choices.end()) result["menuIndex"] = static_cast<int>(it - choices.begin());
        // BUG-054: "added" read as "not applied"; say whether a part was replaced or a new slot filled.
        result["replaced"] = oldPart != nullptr;
        result["applied"] = true;
        if (!warnings.empty()) result["warnings"] = warnings;
        result["note"] = actor->IsPlayerRef()
            ? "Saved with the game. If the change doesn't show, call again with refresh=true or save and load. To undo, set the previous part."
            : "This changes the NPC's base record, so every copy of this NPC changes. NPC faces use pre-baked FaceGen, so the result may not match until the face is regenerated.";
        return result;
    }

    json HeadPartTint(int partType, const std::string& color, bool fromHairColor) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* npc = player ? player->GetActorBase() : nullptr;
        auto* root = player ? player->Get3D(false) : nullptr;
        if (!npc || !root) return {{"error", "The player's 3D isn't loaded"}};

        std::optional<RE::NiColor> wanted;
        if (fromHairColor) {
            // BUG-034: the game renders hair and brows with twice the stored hair colour (seen in game: stored
            // #5E442E, shader #BC885C), so this is what a cell change or load puts back.
            auto* form = npc->headRelatedData ? npc->headRelatedData->hairColor : nullptr;
            if (!form) return {{"error", "The player has no stored hair colour"}};
            wanted = RE::NiColor(form->color.red * 2.0f / 255.0f, form->color.green * 2.0f / 255.0f, form->color.blue * 2.0f / 255.0f);
        } else if (!color.empty()) {
            std::string hex = color[0] == '#' ? color.substr(1) : color;
            unsigned rgb = 0;
            if (hex.size() != 6 || std::sscanf(hex.c_str(), "%6x", &rgb) != 1) return {{"error", "color must be #RRGGBB"}};
            wanted = RE::NiColor(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f);
        }
        auto hex = [](const RE::NiColor& c) {
            auto b = [](float v) { return static_cast<int>(std::clamp(v, 0.0f, 1.0f) * 255.0f + 0.5f); };
            return std::format("#{:02X}{:02X}{:02X}", b(c.red), b(c.green), b(c.blue));
        };

        // The parts of this type, with their extra parts (a hair's hairline, for example).
        std::vector<RE::BGSHeadPart*> selected;
        for (int i = 0; npc->headParts && i < npc->numHeadParts; i++) {
            auto* part = npc->headParts[i];
            if (!part || static_cast<int>(std::to_underlying(part->type.get())) != partType) continue;
            selected.push_back(part);
            for (auto* extra : part->extraParts) {
                if (extra) selected.push_back(extra);
            }
        }
        json parts = json::array();
        for (auto* part : selected) {
            const char* name = part->GetFormEditorID();
            json entry = {{"part", name ? name : ""}, {"formId", std::format("{:08X}", part->GetFormID())}};
            auto* node = (name && name[0]) ? root->GetObjectByName(RE::BSFixedString(name)) : nullptr;
            if (!node) {
                entry["problem"] = "no node with this name in the loaded head";
                parts.push_back(entry);
                continue;
            }
            json geometries = json::array();
            RE::BSVisit::TraverseScenegraphGeometries(node, [&](RE::BSGeometry* geo) {
                json g = {{"geometry", geo->name.c_str()}};
                auto* shader = geo->lightingShaderProp_cast();
                auto* material = shader ? static_cast<RE::BSLightingShaderMaterialBase*>(shader->material) : nullptr;
                if (!material) {
                    g["problem"] = "no lighting shader";
                } else {
                    const auto feature = material->GetFeature();
                    g["material"] = static_cast<int>(feature);
                    if (feature == RE::BSShaderMaterial::Feature::kHairTint) {
                        auto* tint = static_cast<RE::BSLightingShaderMaterialHairTint*>(material);
                        g["tintBefore"] = hex(tint->tintColor);
                        if (wanted) {
                            tint->tintColor = *wanted;
                            g["tint"] = hex(tint->tintColor);
                        }
                    } else {
                        g["problem"] = "not a hair-tint material: its colour comes from the texture, so it can't be tinted";
                    }
                }
                geometries.push_back(g);
                return RE::BSVisit::BSVisitControl::kContinue;
            });
            entry["geometries"] = geometries;
            parts.push_back(entry);
        }
        if (parts.empty()) return {{"error", std::format("The player has no head part of type {}", partType)}};
        json result = {{"parts", parts}, {"set", wanted.has_value()}};
        if (fromHairColor && wanted) result["fromHairColor"] = hex(*wanted);
        return result;
    }

    json RestoreHeadParts(const std::string& refId, const std::vector<std::string>& parts, bool refresh) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* npc = actor->GetActorBase();
        if (!npc) return {{"error", "Actor has no base record"}};
        if (npc->HasOverlays()) return {{"error", "This character uses head part overlays; changing them isn't supported"}};
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Game data not ready"}};

        auto has = [npc](RE::BGSHeadPart* a_part) {
            for (int i = 0; npc->headParts && i < npc->numHeadParts; i++) {
                if (npc->headParts[i] == a_part) return true;
            }
            return false;
        };

        json added = json::array(), present = json::array(), problems = json::array();
        for (const auto& id : parts) {
            const auto bar = id.find('|');
            RE::BGSHeadPart* part = nullptr;
            if (bar != std::string::npos) {
                try {
                    // RaceMenu writes the low 24 bits of the runtime ID, which for a light plugin still holds
                    // the light index (FEBD3820 -> "BD3820"); LookupForm adds the local ID to the plugin's
                    // prefix unmasked, so keep only the plugin's own part (12 bits light, 24 regular).
                    const auto plugin = id.substr(0, bar);
                    auto local = static_cast<RE::FormID>(std::stoul(id.substr(bar + 1), nullptr, 16));
                    if (const auto* file = dataHandler->LookupModByName(plugin)) {
                        local &= file->IsLight() ? 0xFFFu : 0xFFFFFFu;
                        if (auto* form = dataHandler->LookupForm(local, plugin)) part = form->As<RE::BGSHeadPart>();
                    }
                } catch (...) {}
            }
            if (!part) { problems.push_back(id + ": not a loaded head part"); continue; }
            if (part->IsExtraPart()) continue;  // added with its main part
            if (has(part)) { present.push_back(id); continue; }

            const auto type = part->type.get();
            auto* oldPart = type == RE::BGSHeadPart::HeadPartType::kMisc ? nullptr : npc->GetCurrentHeadPartByType(type);
            npc->ChangeHeadPart(part);
            actor->ChangeHeadPart(oldPart, part);
            json entry = DescribePart(part);
            entry["formIdentifier"] = id;
            if (oldPart) entry["replaced"] = DescribePart(oldPart);
            added.push_back(entry);
        }

        if (!added.empty()) {
            npc->AddChange(RE::TESNPC::ChangeFlags::kFace);
            if (refresh) actor->DoReset3D(false);
        }
        json result = {{"actor", ActorLabel(actor)}, {"added", added}, {"alreadyPresent", present.size()}};
        if (!problems.empty()) result["problems"] = problems;
        return result;
    }

    json RemoveHeadPart(const std::string& refId, const std::string& part, const std::string& type, bool refresh) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* npc = actor->GetActorBase();
        if (!npc) return {{"error", "Actor has no base record"}};
        if (npc->HasOverlays()) return {{"error", "This character uses head part overlays; changing them isn't supported"}};
        if (!npc->headParts || npc->numHeadParts <= 0) return {{"error", "This character has no head parts"}};

        RE::BGSHeadPart* target = nullptr;
        if (!part.empty()) {
            std::string error;
            target = FindPart(npc, part, "", -1, error);
            if (!target) return {{"error", error}};
        } else {
            const int t = HeadPartTypeFromName(type);
            if (t < 0) return {{"error", "Pass part (FormID or editor ID, from GetAppearance or the current list), or type"}};
            target = npc->GetCurrentHeadPartByType(static_cast<RE::BGSHeadPart::HeadPartType>(t));
            if (target && target->type.get() == RE::BGSHeadPart::HeadPartType::kMisc) {
                // Several misc parts can be worn: make the caller say which.
                return {{"error", "Several misc parts can be worn at once; pass part with the FormID of the one to remove"}, {"current", CurrentParts(npc)}};
            }
        }
        if (!target) return {{"error", "The character has no part of that type"}, {"current", CurrentParts(npc)}};
        const auto t = target->type.get();
        if (t == RE::BGSHeadPart::HeadPartType::kFace || t == RE::BGSHeadPart::HeadPartType::kEyes) {
            return {{"error", "The face and eyes can't be removed, only replaced (SetHeadPart)"}};
        }

        // Remove it from the record by closing the gap: the game owns the array and frees it
        // whole, so it is shortened in place rather than reallocated. Its extra parts (a hair's
        // hairline, for example) are not in this list; they are rebuilt from the remaining parts.
        int at = -1;
        for (std::int8_t i = 0; i < npc->numHeadParts; ++i) {
            if (npc->headParts[i] == target) { at = i; break; }
        }
        if (at < 0) return {{"error", EditorIdOf(target) + " isn't one of this character's head parts"}, {"current", CurrentParts(npc)}};
        for (std::int8_t i = static_cast<std::int8_t>(at); i + 1 < npc->numHeadParts; ++i) npc->headParts[i] = npc->headParts[i + 1];
        npc->headParts[npc->numHeadParts - 1] = nullptr;
        --npc->numHeadParts;
        npc->AddChange(RE::TESNPC::ChangeFlags::kFace);

        // The loaded head keeps the old mesh until its 3D is rebuilt.
        if (refresh) actor->DoReset3D(false);

        json result;
        result["actor"] = ActorLabel(actor);
        result["removed"] = DescribePart(target);
        result["current"] = CurrentParts(npc);
        result["refreshed3D"] = refresh;
        result["note"] = refresh
            ? "Removed from the record and the 3D was rebuilt. If it still shows, save and load. To undo, SetHeadPart with the removed part."
            : "Removed from the record (saved with the game). It stays visible until the 3D is rebuilt: call again with refresh=true, or save and load.";
        return result;
    }

    json ExcludedHeadParts(const std::string& refId, const std::string& type, int start, int count,
                           const std::string& rule, const std::string& explain) {
        auto* actor = Helpers::ResolveActor(refId);
        if (!actor) return {{"error", "Actor not found: " + refId}};
        auto* npc = actor->GetActorBase();
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        const int t = HeadPartTypeFromName(type);
        if (!npc || !dataHandler || t < 0) return {{"error", "type must be hair, eyes, eyebrows, facialHair, scar, face or misc"}};
        auto* race = npc->GetRace();
        const bool female = npc->GetSex() == RE::SEX::kFemale;

        // The menu list in editor ID order, to say where each excluded part would sort.
        auto shown = HeadPartChoices(npc, t, "editorId");
        std::vector<std::string> shownIds;
        shownIds.reserve(shown.size());
        for (auto* p : shown) shownIds.push_back(EditorIdOf(p));

        auto reasonsFor = [&](RE::BGSHeadPart* part) {
            json reasons = json::array();
            if (part->IsExtraPart()) reasons.push_back("extraPart");
            if (!part->flags.all(RE::BGSHeadPart::Flag::kPlayable)) reasons.push_back("notPlayable");
            if (!SexMatches(part, female)) reasons.push_back("otherSex");
            if (race && part->validRaces && !part->validRaces->HasForm(race)) reasons.push_back("raceNotInValidRaces");
            return reasons;
        };
        auto describe = [&](RE::BGSHeadPart* part, const json& reasons) {
            json e = DescribePart(part);
            e["reasons"] = reasons;
            e["wouldBeIndex"] = static_cast<int>(std::lower_bound(shownIds.begin(), shownIds.end(), EditorIdOf(part)) - shownIds.begin());
            e["validRaces"] = part->validRaces ? json(std::format("{:08X}", part->validRaces->GetFormID())) : json(nullptr);
            e["playable"] = part->flags.all(RE::BGSHeadPart::Flag::kPlayable);
            return e;
        };

        // One part by editor ID or FormID: every rule it fails, whatever its type.
        if (!explain.empty()) {
            RE::TESForm* form = nullptr;
            try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(explain)); } catch (...) {}
            if (!form) form = RE::TESForm::LookupByEditorID(explain);
            auto* part = form ? form->As<RE::BGSHeadPart>() : nullptr;
            if (!part) return {{"error", "No head part '" + explain + "'"}};
            auto reasons = reasonsFor(part);
            auto e = describe(part, reasons);
            e["shownInList"] = reasons.empty() && static_cast<int>(std::to_underlying(part->type.get())) == t;
            return {{"explain", e}};
        }

        std::vector<json> all;
        for (auto* part : dataHandler->GetFormArray<RE::BGSHeadPart>()) {
            if (!part || static_cast<int>(std::to_underlying(part->type.get())) != t) continue;
            auto reasons = reasonsFor(part);
            if (reasons.empty()) continue;
            if (!rule.empty() && std::find(reasons.begin(), reasons.end(), rule) == reasons.end()) continue;
            all.push_back(describe(part, reasons));
        }
        // Fewest failed rules first (the likeliest to be shown by a laxer menu), then by sort position.
        std::stable_sort(all.begin(), all.end(), [](const json& a, const json& b) {
            if (a["reasons"].size() != b["reasons"].size()) return a["reasons"].size() < b["reasons"].size();
            return a["wouldBeIndex"].get<int>() < b["wouldBeIndex"].get<int>();
        });
        count = std::clamp(count, 1, 200);
        start = std::clamp(start, 0, static_cast<int>(all.size()));
        json page = json::array();
        for (int i = start; i < static_cast<int>(all.size()) && i < start + count; ++i) page.push_back(all[i]);
        return {{"type", type}, {"shown", shown.size()}, {"excludedTotal", all.size()}, {"start", start}, {"entries", page},
                {"note", "Head parts of this type the menu list leaves out, fewest failed rules first, with the index each would "
                         "take. rule filters by one reason; explain=<editorId> says why one part is left out."}};
    }

}

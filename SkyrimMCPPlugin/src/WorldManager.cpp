#include "WorldManager.h"
#include "Helpers.h"
#include "TaskQueue.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstring>
#include <format>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace SkyrimMCP::WorldManager {

    using json = nlohmann::json;

    namespace {
        const char* WeatherCategory(const RE::TESWeather* weather) {
            const auto flags = weather->data.flags;
            if (flags.all(RE::TESWeather::WeatherDataFlag::kSnow)) return "snow";
            if (flags.all(RE::TESWeather::WeatherDataFlag::kRainy)) return "rain";
            if (flags.all(RE::TESWeather::WeatherDataFlag::kCloudy)) return "cloudy";
            if (flags.all(RE::TESWeather::WeatherDataFlag::kPleasant)) return "clear";
            return "other";
        }

        // Most weathers have no display name, and editor IDs are only kept in memory when a
        // mod such as powerofthree's Tweaks loads them. "name" is never blank: display name,
        // then editor ID, then "<category> weather <formId>".
        json DescribeWeather(RE::TESWeather* weather) {
            json w;
            const auto formId = std::format("{:08X}", weather->GetFormID());
            const char* displayName = weather->GetName();
            const char* editorId = weather->GetFormEditorID();
            const char* category = WeatherCategory(weather);

            w["formId"] = formId;
            w["category"] = category;
            if (displayName && displayName[0]) w["displayName"] = displayName;
            if (editorId && editorId[0]) w["editorId"] = editorId;
            if (auto* file = weather->GetFile(0)) w["mod"] = file->fileName;

            if (displayName && displayName[0]) w["name"] = displayName;
            else if (editorId && editorId[0]) w["name"] = editorId;
            else w["name"] = std::format("{} weather {}", category, formId);
            return w;
        }
    }

    // FEAT-258: a reference in the target cell to MoveTo. Recorded in game (2026-11-20): Teleport("back"), a native MoveTo to a
    // marker reference, draws the real loading screen every time, while coc and CenterOnCell only freeze the frame. Prefers a
    // map marker, then a heading marker (COC markers are these), then a door, then a plain XMarker; persistent references
    // are the ones in memory for a cell that isn't loaded (an exterior's live in its worldspace's persistent cell).
    RE::TESObjectREFR* MoveTargetIn(RE::TESObjectCELL* cell, std::string& how) {
        RE::TESObjectREFR* best = nullptr;
        int bestScore = 0;
        auto consider = [&](RE::TESObjectREFR* r) {
            if (!r || r->IsDisabled() || r->IsDeleted()) return;
            auto* base = r->GetBaseObject();
            if (!base) return;
            int score = 0;
            if (r->extraList.HasType<RE::ExtraMapMarker>()) score = 5;
            else if (base->GetFormID() == 0x00000034) score = 4;   // XMarkerHeading
            else if (base->Is(RE::FormType::Door)) score = 3;
            else if (base->GetFormID() == 0x0000003B) score = 2;   // XMarker
            if (score > bestScore) { bestScore = score; best = r; }
        };
        if (cell->IsInteriorCell()) {
            cell->ForEachReference([&](RE::TESObjectREFR* r) { consider(r); return RE::BSContainer::ForEachResult::kContinue; });
        } else if (auto* coords = cell->GetCoordinates()) {
            const int cx = coords->cellX, cy = coords->cellY;
            auto inCell = [&](RE::TESObjectREFR* r) {
                const auto pos = r->GetPosition();
                return static_cast<int>(std::floor(pos.x / 4096.0f)) == cx && static_cast<int>(std::floor(pos.y / 4096.0f)) == cy;
            };
            cell->ForEachReference([&](RE::TESObjectREFR* r) { consider(r); return RE::BSContainer::ForEachResult::kContinue; });
            if (auto* ws = cell->GetRuntimeData().worldSpace; ws && ws->persistentCell) {
                ws->persistentCell->ForEachReference([&](RE::TESObjectREFR* r) {
                    if (r && inCell(r)) consider(r);
                    return RE::BSContainer::ForEachResult::kContinue;
                });
            }
        }
        static const char* kKinds[] = {"", "", "marker", "door", "heading marker", "map marker"};
        if (best) how = std::format("{} {:08X}", kKinds[bestScore], best->GetFormID());
        return best;
    }

    json Teleport(const std::string& cellId, const std::string& method) {
        // coc is kept as console command — MoveTo_Impl is private, and coc handles
        // all cell loading/transition logic (loading screens, cell attach, navmesh)
        // that direct MoveTo doesn't. coc is a valid script command, not batch.
        // BUG-070: coc to a cell that doesn't exist did nothing and the tool still reported success.
        // coc takes a cell's editor ID (and a few worldspace names), which the game keeps; any form with that editor ID
        // counts, so nothing coc accepts is refused.
        // BUG-073: a teleport ran with the map open. Refuse while a menu pauses the game, as captures do (BUG-057).
        if (const auto blocked = Helpers::MoveBlocker(); !blocked.empty()) return {{"error", blocked}};
        auto* target = RE::TESForm::LookupByEditorID(cellId);
        if (!target) {
            return {{"error", "No cell with the editor ID '" + cellId + "'. Find one with SearchForms(type: 'cell') or use a known location name."}};
        }
        // BUG-071: coc runs the loading screen inside the call, so a slow load timed the request out though the move worked.
        // Start it on the next frame and return at once; the server waits for the cell to change.
        // BUG-072: mark the starting spot (as StudioGo does) so Teleport("back") can return without raw console moves.
        json markerId = nullptr;
        if (auto* player = RE::PlayerCharacter::GetSingleton()) {
            if (auto* xmarker = RE::TESForm::LookupByID<RE::TESBoundObject>(0x0000003B)) {
                if (auto marker = player->PlaceObjectAtMe(xmarker, true)) {
                    marker->SetPosition(player->GetPosition());
                    marker->data.angle = player->data.angle;
                    markerId = std::format("{:08X}", marker->GetFormID());
                }
            }
        }
        // FEAT-258: how to move. coc (default, as before): the console command, which freezes the frame during the load.
        // center: the native CenterOnCell coc itself calls, without the console. loadingscreen: show the Loading Menu, then
        // CenterOnCell on a later frame, so the game may draw its loading screen as fast travel does.
        // FEAT-258: 'auto' (default) moves to a reference in the cell, as Teleport("back") does, which shows the loading
        // screen; with none found, or method 'coc', the console move (frozen frame).
        std::string used = "coc";
        std::string how;
        RE::TESObjectREFR* to = nullptr;
        if (method != "coc") {
            if (auto* cell = target->As<RE::TESObjectCELL>()) to = MoveTargetIn(cell, how);
        }
        if (to) {
            used = "marker";
            const RE::FormID toId = to->GetFormID();
            TaskQueue::Post([toId]() {
                auto* p = RE::PlayerCharacter::GetSingleton();
                auto* ref = RE::TESForm::LookupByID<RE::TESObjectREFR>(toId);
                if (p && ref) p->MoveTo(ref);
            });
        } else {
            TaskQueue::Post([cellId]() { Helpers::ExecuteConsoleCommand("coc " + cellId); });
        }
        json result = {{"moving", true}, {"cell", cellId}, {"targetFormId", std::format("{:08X}", target->GetFormID())},
                       {"markerId", markerId}, {"method", used}};
        if (!how.empty()) result["movedTo"] = how;
        else if (method != "coc") result["note"] = "No marker or door found in that cell: used the console move (a frozen frame instead of a loading screen).";
        if (const char* name = target->GetName(); name && name[0]) result["targetName"] = name;
        return result;
    }

    json GetWeather() {
        auto* sky = RE::Sky::GetSingleton();
        if (!sky) return {{"error", "Sky not available"}};

        json result;
        if (sky->currentWeather) {
            auto current = DescribeWeather(sky->currentWeather);
            result["current"] = current["name"];
            result["currentFormId"] = current["formId"];
            result["currentWeather"] = current;
        }
        if (sky->lastWeather) {
            auto previous = DescribeWeather(sky->lastWeather);
            result["previous"] = previous["name"];
            result["previousWeather"] = previous;
        }
        result["weatherPct"] = sky->currentWeatherPct;

        return result;
    }

    json SetWeather(const std::string& weatherFormIdOrName) {
        auto* sky = RE::Sky::GetSingleton();
        if (!sky) return {{"error", "Sky not available"}};

        // Try FormID first
        RE::TESWeather* weather = nullptr;
        try {
            auto formId = Helpers::ParseFormId(weatherFormIdOrName);
            weather = RE::TESForm::LookupByID<RE::TESWeather>(formId);
        } catch (...) {}

        // Try by category keyword (snow, rain, clear, cloudy) using weather data flags
        if (!weather) {
            std::string lower = weatherFormIdOrName;
            std::transform(lower.begin(), lower.end(), lower.begin(), ::tolower);

            // Map keywords to weather flags
            std::optional<RE::TESWeather::WeatherDataFlag> targetFlag;
            if (lower == "snow" || lower == "snowy") targetFlag = RE::TESWeather::WeatherDataFlag::kSnow;
            else if (lower == "rain" || lower == "rainy") targetFlag = RE::TESWeather::WeatherDataFlag::kRainy;
            else if (lower == "clear" || lower == "sunny" || lower == "pleasant") targetFlag = RE::TESWeather::WeatherDataFlag::kPleasant;
            else if (lower == "cloudy" || lower == "overcast" || lower == "cloud") targetFlag = RE::TESWeather::WeatherDataFlag::kCloudy;

            auto* dataHandler = RE::TESDataHandler::GetSingleton();
            if (dataHandler) {
                for (auto* form : dataHandler->GetFormArray<RE::TESWeather>()) {
                    if (!form) continue;

                    // Match by category flag
                    if (targetFlag.has_value() && form->data.flags.all(*targetFlag)) {
                        // Prefer Skyrim.esm weathers for basic categories
                        auto* file = form->GetFile(0);
                        if (file && std::string(file->fileName) == "Skyrim.esm") {
                            weather = form;
                            break;
                        }
                        if (!weather) weather = form;  // take first match as fallback
                        continue;
                    }

                    // Also try name/editorId/formId matching
                    std::string name = form->GetName() ? form->GetName() : "";
                    std::string lowerName = name;
                    std::transform(lowerName.begin(), lowerName.end(), lowerName.begin(), ::tolower);

                    std::string editorId = form->GetFormEditorID() ? form->GetFormEditorID() : "";
                    std::string lowerEditorId = editorId;
                    std::transform(lowerEditorId.begin(), lowerEditorId.end(), lowerEditorId.begin(), ::tolower);

                    if ((!lowerName.empty() && lowerName.find(lower) != std::string::npos) ||
                        (!lowerEditorId.empty() && lowerEditorId.find(lower) != std::string::npos)) {
                        weather = form;
                        break;
                    }
                }
            }
        }

        if (!weather) return {{"error", "Weather not found: " + weatherFormIdOrName}};

        sky->ForceWeather(weather, true);
        auto described = DescribeWeather(weather);
        return {{"set", true}, {"weather", described["name"]}, {"formId", described["formId"]},
                {"category", described["category"]}};
    }

    json ListWeathers() {
        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};

        json weathers = json::array();

        for (auto* form : dataHandler->GetFormArray<RE::TESWeather>()) {
            if (!form) continue;
            try {
                json w = DescribeWeather(form);  // category comes from the weather's data flags
                w["windSpeed"] = form->data.windSpeed;

                weathers.push_back(w);
            } catch (...) {
                continue;
            }
        }

        return {{"weathers", weathers}, {"count", weathers.size()}};
    }

    json GetCellInfo() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* cell = player->GetParentCell();
        if (!cell) return {{"error", "Cell not available"}};

        json result;
        result["name"] = cell->GetName() ? cell->GetName() : "unnamed";
        result["formId"] = std::format("{:08X}", cell->GetFormID());
        result["isInterior"] = cell->IsInteriorCell();
        result["isExterior"] = cell->IsExteriorCell();

        auto* worldspace = player->GetWorldspace();
        if (worldspace) {
            result["worldspace"] = worldspace->GetName();
            result["worldspaceFormId"] = std::format("{:08X}", worldspace->GetFormID());
        }

        auto pos = player->GetPosition();
        result["posX"] = pos.x;
        result["posY"] = pos.y;
        result["posZ"] = pos.z;

        auto angle = player->GetAngle();
        result["angleX"] = angle.x;
        result["angleY"] = angle.y;
        result["angleZ"] = angle.z;

        return result;
    }

    json GetNearbyObjects(float radius, const std::string& typeFilter) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* cell = player->GetParentCell();
        if (!cell) return {{"error", "Cell not available"}};

        auto playerPos = player->GetPosition();
        json objects = json::array();

        std::string lowerFilter = typeFilter;
        std::transform(lowerFilter.begin(), lowerFilter.end(), lowerFilter.begin(), ::tolower);

        cell->ForEachReferenceInRange(playerPos, radius, [&](RE::TESObjectREFR* refPtr) -> RE::BSContainer::ForEachResult {
            if (!refPtr || refPtr == player) return RE::BSContainer::ForEachResult::kContinue;
            auto& ref = *refPtr;

            try {
                auto* baseObj = ref.GetBaseObject();
                if (!baseObj) return RE::BSContainer::ForEachResult::kContinue;

                std::string typeStr;
                bool include = lowerFilter.empty() || lowerFilter == "all";

                switch (baseObj->GetFormType()) {
                    case RE::FormType::Container:
                        typeStr = "container";
                        if (lowerFilter == "container" || lowerFilter == "chest") include = true;
                        break;
                    case RE::FormType::Door:
                        typeStr = "door";
                        if (lowerFilter == "door") include = true;
                        break;
                    case RE::FormType::Furniture:
                        typeStr = "furniture";
                        if (lowerFilter == "furniture" || lowerFilter == "crafting") include = true;
                        break;
                    case RE::FormType::Activator:
                        typeStr = "activator";
                        if (lowerFilter == "activator") include = true;
                        break;
                    case RE::FormType::Flora:
                        typeStr = "flora";
                        if (lowerFilter == "flora" || lowerFilter == "plant") include = true;
                        break;
                    case RE::FormType::Light:
                        typeStr = "light";
                        if (lowerFilter == "light") include = true;
                        break;
                    case RE::FormType::Static:
                        typeStr = "static";
                        if (lowerFilter == "static") include = true;
                        break;
                    default:
                        if (ref.As<RE::Actor>()) return RE::BSContainer::ForEachResult::kContinue;
                        typeStr = "other";
                        break;
                }

                if (!include) return RE::BSContainer::ForEachResult::kContinue;

                auto refPos = ref.GetPosition();
                float dx = refPos.x - playerPos.x;
                float dy = refPos.y - playerPos.y;
                float dz = refPos.z - playerPos.z;
                float distance = std::sqrt(dx * dx + dy * dy + dz * dz);

                json obj;
                obj["refId"] = std::format("{:08X}", ref.GetFormID());
                obj["baseId"] = std::format("{:08X}", baseObj->GetFormID());
                obj["name"] = ref.GetName() ? ref.GetName() : (baseObj->GetName() ? baseObj->GetName() : "");
                obj["type"] = typeStr;
                obj["distance"] = distance;
                obj["isLocked"] = ref.IsLocked();

                objects.push_back(obj);
            } catch (...) {}

            return RE::BSContainer::ForEachResult::kContinue;
        });

        return {{"objects", objects}, {"count", objects.size()}};
    }

    json UnlockDoor(const std::string& refFormIdHex) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
            if (!form) return {{"error", "Reference not found: " + refFormIdHex}};

            auto* ref = form->As<RE::TESObjectREFR>();
            if (!ref) return {{"error", "Not a reference: " + refFormIdHex}};

            auto* lock = ref->GetLock();
            if (!lock) return {{"error", "Object has no lock"}};
            if (!lock->IsLocked()) return {{"error", "Already unlocked"}};

            lock->SetLocked(false);
            return {{"unlocked", true}, {"refId", refFormIdHex}, {"name", ref->GetName() ? ref->GetName() : ""}};
        } catch (...) {
            return {{"error", "Failed to unlock: " + refFormIdHex}};
        }
    }

    json LockDoor(const std::string& refFormIdHex, int lockLevel) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
            if (!form) return {{"error", "Reference not found: " + refFormIdHex}};

            auto* ref = form->As<RE::TESObjectREFR>();
            if (!ref) return {{"error", "Not a reference: " + refFormIdHex}};

            auto* lock = ref->GetLock();
            if (!lock) return {{"error", "Object has no lock"}};

            lock->SetLocked(true);
            lock->baseLevel = static_cast<std::int8_t>(lockLevel);
            return {{"locked", true}, {"refId", refFormIdHex}, {"lockLevel", lockLevel}};
        } catch (...) {
            return {{"error", "Failed to lock: " + refFormIdHex}};
        }
    }

    json GetContainerInventory(const std::string& refFormIdHex) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
            if (!form) return {{"error", "Reference not found: " + refFormIdHex}};

            auto* ref = form->As<RE::TESObjectREFR>();
            if (!ref) return {{"error", "Not a reference: " + refFormIdHex}};

            auto inv = ref->GetInventory();
            json items = json::array();

            for (auto& [itemForm, data] : inv) {
                if (!itemForm || data.first <= 0) continue;
                try {
                    json item;
                    item["formId"] = std::format("{:08X}", itemForm->GetFormID());
                    item["name"] = itemForm->GetName();
                    item["count"] = data.first;

                    if (auto* boundObj = itemForm->As<RE::TESBoundObject>()) {
                        item["weight"] = boundObj->GetWeight();
                        item["value"] = boundObj->GetGoldValue();
                    }
                    items.push_back(item);
                } catch (...) { continue; }
            }

            return {{"refId", refFormIdHex}, {"name", ref->GetName() ? ref->GetName() : ""},
                    {"isLocked", ref->IsLocked()}, {"items", items}, {"count", items.size()}};
        } catch (...) {
            return {{"error", "Failed to read container: " + refFormIdHex}};
        }
    }


    namespace {
        bool Blocks(RE::TESObjectREFR* ref) {
            auto* base = ref ? ref->GetBaseObject() : nullptr;
            if (!base || ref->IsDisabled() || ref->IsDeleted() || ref->Is(RE::FormType::ActorCharacter)) return false;
            switch (base->GetFormType()) {
            case RE::FormType::Static: case RE::FormType::MovableStatic: case RE::FormType::Tree: case RE::FormType::Furniture:
            case RE::FormType::Container: case RE::FormType::Door: case RE::FormType::Activator: case RE::FormType::Flora:
                return ref->GetBoundMax().x - ref->GetBoundMin().x > 40.0f;  // markers and pebbles don't count
            default:
                return false;
            }
        }
    }

    json FindOpenSpace(float searchRadius, float size) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* tes = RE::TES::GetSingleton();
        auto* cell = player ? player->GetParentCell() : nullptr;
        if (!player || !tes || !cell) return {{"error", "No player in the world"}};
        if (cell->IsInteriorCell()) return {{"error", "The player is indoors: go outside first"}};
        searchRadius = std::clamp(searchRadius, 0.0f, 4000.0f);
        size = std::clamp(size, 300.0f, 3000.0f);
        struct Blocker { RE::NiPoint3 at; float radius; std::string what; };
        std::vector<Blocker> blockers;  // a rock's origin can be far outside the square while its mesh covers it: count its size
        tes->ForEachReferenceInRange(player, searchRadius + size, [&](RE::TESObjectREFR* ref) {
            if (Blocks(ref)) {
                const auto span = ref->GetBoundMax() - ref->GetBoundMin();
                auto* b = ref->GetBaseObject();
                const char* ed = b->GetFormEditorID();
                blockers.push_back({ ref->GetPosition(), 0.5f * std::max(span.x, span.y) * ref->GetScale(),
                                     std::format("{} {:08X}{}{} r{:.0f}", RE::FormTypeToString(b->GetFormType()), b->GetFormID(), ed && *ed ? " " : "", ed ? ed : "", 0.5f * std::max(span.x, span.y) * ref->GetScale()) });
            }
            return RE::BSContainer::ForEachResult::kContinue;
        });
        const auto origin = player->GetPosition();
        json spots = json::array();
        for (float r = 0; r <= searchRadius; r += 400.0f) {
            const int steps = r == 0 ? 1 : 12;
            for (int i = 0; i < steps; ++i) {
                const float a = 6.2831853f * i / steps;
                const RE::NiPoint3 c{ origin.x + r * std::sin(a), origin.y + r * std::cos(a), origin.z };
                float lo = 1e9f, hi = -1e9f;
                bool land = true;
                for (int gx = -2; gx <= 2 && land; ++gx)
                    for (int gy = -2; gy <= 2; ++gy) {
                        float h = 0;
                        if (!tes->GetLandHeight({ c.x + gx * size / 4, c.y + gy * size / 4, c.z + 5000.0f }, h)) { land = false; break; }
                        lo = std::min(lo, h); hi = std::max(hi, h);
                    }
                if (!land) continue;
                int clutter = 0;
                json what = json::array();
                for (const auto& b : blockers) if (std::hypot(b.at.x - c.x, b.at.y - c.y) < size * 0.6f + b.radius) { ++clutter; if (what.size() < 5) what.push_back(b.what); }
                float ground = 0; tes->GetLandHeight({ c.x, c.y, c.z + 5000.0f }, ground);
                spots.push_back({{"x", c.x}, {"y", c.y}, {"z", ground}, {"slope", hi - lo}, {"objects", clutter}, {"what", what}, {"fromPlayer", r}});
            }
        }
        std::sort(spots.begin(), spots.end(), [](const json& a, const json& b) {
            const float sa = a["slope"].get<float>() + a["objects"].get<int>() * 60.0f, sb = b["slope"].get<float>() + b["objects"].get<int>() * 60.0f;
            return sa < sb;
        });
        if (spots.size() > 6) spots.erase(spots.begin() + 6, spots.end());
        return {{"size", size}, {"spots", spots}, {"note", "slope: highest minus lowest ground over the square (game units); objects: buildings, trees, rocks and furniture in it. Water isn't checked."}};
    }

    namespace {
        // "Plugin.esp|0B11AC" (stays right when the load order changes) or a form ID.
        RE::TESForm* FormFromId(const std::string& id) {
            if (const auto bar = id.rfind('|'); bar != std::string::npos) {
                auto* data = RE::TESDataHandler::GetSingleton();
                try { return data ? data->LookupForm(static_cast<RE::FormID>(std::stoul(id.substr(bar + 1), nullptr, 16)), id.substr(0, bar)) : nullptr; } catch (...) { return nullptr; }
            }
            try { return RE::TESForm::LookupByID(Helpers::ParseFormId(id)); } catch (...) { return nullptr; }
        }
        std::string StableIdOf(RE::TESForm* form) {
            auto* file = form ? form->GetFile(0) : nullptr;
            return file ? std::format("{}|{:06X}", file->GetFilename(), form->GetLocalFormID()) : std::format("{:08X}", form ? form->GetFormID() : 0);
        }
    }

    json PlacedObjects(const std::string& centerRef, float radius) {
        auto* center = FormFromId(centerRef);
        auto* at = center ? center->As<RE::TESObjectREFR>() : nullptr;
        auto* tes = RE::TES::GetSingleton();
        if (!at || !tes) return {{"error", "Not a reference: " + centerRef}};
        const auto c = at->GetPosition();
        radius = std::clamp(radius, 100.0f, 6000.0f);
        json pieces = json::array();
        std::unordered_set<RE::FormID> seen;
        auto visit = [&](RE::TESObjectREFR* ref) {
            auto* base = ref ? ref->GetBaseObject() : nullptr;
            if (!base || ref == at || (ref->GetFormID() >> 24) != 0xFF || ref->IsDisabled() || ref->IsDeleted() || ref->Is(RE::FormType::ActorCharacter)) return RE::BSContainer::ForEachResult::kContinue;
            if (ref->GetPosition().GetDistance(c) > radius || !seen.insert(ref->GetFormID()).second) return RE::BSContainer::ForEachResult::kContinue;
            const auto p = ref->GetPosition();
            const char* ed = base->GetFormEditorID();
            pieces.push_back({{"refId", std::format("{:08X}", ref->GetFormID())}, {"base", StableIdOf(base)}, {"type", RE::FormTypeToString(base->GetFormType())},
                              {"what", ed && *ed ? json(ed) : json(base->GetName() && *base->GetName() ? base->GetName() : "")},
                              {"offset", { std::round((p.x - c.x) * 10) / 10, std::round((p.y - c.y) * 10) / 10, std::round((p.z - c.z) * 10) / 10 }},
                              {"angle", std::round(ref->GetAngleZ() * 57.2957795f * 10) / 10}, {"scale", ref->GetScale()}});
            return RE::BSContainer::ForEachResult::kContinue;
        };
        // Pieces are placed persistent: in an exterior they live in the worldspace's persistent cell, which is always in memory,
        // while the range search only reaches loaded cells (the player 14000 units away found nothing, 2026-11-23).
        if (auto* world = at->GetWorldspace(); world && world->persistentCell) world->persistentCell->ForEachReference(visit);
        if (auto* cell = at->GetParentCell(); cell && cell->IsInteriorCell()) cell->ForEachReference(visit);
        tes->ForEachReferenceInRange(at, radius, visit);
        return {{"center", { c.x, c.y, c.z }}, {"facing", at->GetAngleZ() * 57.2957795f}, {"pieces", pieces}};
    }

    json PlaceObject(const std::string& baseId, float x, float y, float z, float angleZ, bool onGround) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        RE::TESForm* form = FormFromId(baseId);
        auto* base = form ? form->As<RE::TESBoundObject>() : nullptr;
        if (!player || !base) return {{"error", "Not a placeable base object: " + baseId}};
        if (onGround) if (auto* tes = RE::TES::GetSingleton()) { float h = 0; if (tes->GetLandHeight({ x, y, z + 5000.0f }, h)) z = h; }
        auto ref = player->PlaceObjectAtMe(base, true);  // persistent: kept by saves wherever the player goes
        if (!ref) return {{"error", "PlaceObjectAtMe returned nothing"}};
        const float a = angleZ * 0.0174532925f;
        ref->SetAngle({ 0.0f, 0.0f, a });
        // The ground under the whole footprint, not just the centre: a big piece goes to its lowest corner, a little sunk, as the
        // game's own buildings are (on a slope the low side floated and the high side sank, 2026-11-23); a small one to its centre.
        const auto lo = ref->GetBoundMin(), hi = ref->GetBoundMax();
        const float span = std::max(hi.x - lo.x, hi.y - lo.y) * ref->GetScale();
        float low = z, high = z;
        if (onGround) if (auto* tes = RE::TES::GetSingleton()) {
            low = 1e9f; high = -1e9f;
            float sum = 0; int samples = 0;
            for (const auto& [lx, ly] : std::initializer_list<std::pair<float, float>>{ { lo.x, lo.y }, { lo.x, hi.y }, { hi.x, lo.y }, { hi.x, hi.y }, { 0, 0 } }) {
                const float wx = x + lx * std::cos(a) + ly * std::sin(a), wy = y - lx * std::sin(a) + ly * std::cos(a);
                float h = 0;
                if (tes->GetLandHeight({ wx, wy, z + 5000.0f }, h)) { low = std::min(low, h); high = std::max(high, h); sum += h; ++samples; }
            }
            if (samples == 0) { low = high = sum = z; samples = 1; }
            float centre = z; tes->GetLandHeight({ x, y, z + 5000.0f }, centre);
            // Lowest corner buried the uphill side of a farmhouse on a 290-unit slope (Cory: "it's in the ground"); about 60% of the way
            // up looked right, which the average of the corners and centre gives on an even slope. A little sunk hides the edges.
            z = span > 300.0f ? sum / samples - 15.0f : centre;
        }
        ref->SetPosition(x, y, z);
        return {{"refId", std::format("{:08X}", ref->GetFormID())}, {"base", std::format("{:08X}", base->GetFormID())}, {"x", x}, {"y", y}, {"z", z},
                {"footprint", span}, {"groundRise", high - low}};
    }

    // FEAT-307: the land's height under each point (the terrain only: rocks and other objects aren't counted), so a home layout
    // can stand each piece on its own ground. Null where there's no land (out of the loaded area).
    json GroundHeights(const json& points) {
        auto* tes = RE::TES::GetSingleton();
        json heights = json::array();
        for (const auto& p : points) {
            float h = 0;
            if (tes && p.is_array() && p.size() >= 2 && p[0].is_number() && p[1].is_number() &&
                tes->GetLandHeight({ p[0].get<float>(), p[1].get<float>(), 100000.0f }, h)) heights.push_back(h);
            else heights.push_back(nullptr);
        }
        return {{"heights", heights}};
    }

    // FEAT-307: a site survey for a home layout, before anything is placed (Cory, 2026-11-24: "initial placement should be a bit
    // more resilient to the terrain"). For each piece's spot (x, y, base, angle): the land's rise under its footprint and the objects
    // in the way (rocks, trees, buildings: as FindOpenSpace counts them). Land only: a rock's top isn't ground here, it's in the way.
    json GroundSurvey(const json& points) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* tes = RE::TES::GetSingleton();
        if (!player || !tes) return {{"error", "No player in the world"}};
        if (auto* cell = player->GetParentCell(); !cell || cell->IsInteriorCell()) return {{"error", "The player is indoors: go outside first"}};
        const auto origin = player->GetPosition();
        float reach = 0;
        for (const auto& p : points)
            if (p.is_object() && p.contains("x") && p.contains("y"))
                reach = std::max(reach, std::hypot(p["x"].get<float>() - origin.x, p["y"].get<float>() - origin.y));
        struct Blocker { RE::NiPoint3 at; float radius; std::string what; };
        std::vector<Blocker> blockers;
        tes->ForEachReferenceInRange(player, std::min(reach + 1500.0f, 8000.0f), [&](RE::TESObjectREFR* ref) {
            if (Blocks(ref) && (ref->GetFormID() >> 24) != 0xFF) {  // SkyLink's own pieces (an old home being rebuilt) don't count
                const auto span = ref->GetBoundMax() - ref->GetBoundMin();
                auto* b = ref->GetBaseObject();
                const char* name = b->GetName();
                const char* ed = b->GetFormEditorID();
                blockers.push_back({ ref->GetPosition(), 0.5f * std::max(span.x, span.y) * ref->GetScale(),
                                     name && *name ? name : ed && *ed ? ed : std::string(RE::FormTypeToString(b->GetFormType())) });
            }
            return RE::BSContainer::ForEachResult::kContinue;
        });
        json out = json::array();
        for (const auto& p : points) {
            if (!p.is_object() || !p.contains("x") || !p.contains("y")) { out.push_back(nullptr); continue; }
            const float x = p["x"].get<float>(), y = p["y"].get<float>();
            const float a = p.value("angle", 0.0f) * 0.0174532925f;
            auto* form = FormFromId(p.value("base", std::string()));
            auto* base = form ? form->As<RE::TESBoundObject>() : nullptr;
            float lx0 = -50, ly0 = -50, lx1 = 50, ly1 = 50;
            if (base) {
                lx0 = base->boundData.boundMin.x; ly0 = base->boundData.boundMin.y;
                lx1 = base->boundData.boundMax.x; ly1 = base->boundData.boundMax.y;
            }
            const float radius = 0.5f * std::max(lx1 - lx0, ly1 - ly0);
            float low = 1e9f, high = -1e9f, centre = 0;
            bool land = true;
            for (const auto& [lx, ly] : std::initializer_list<std::pair<float, float>>{ { lx0, ly0 }, { lx0, ly1 }, { lx1, ly0 }, { lx1, ly1 }, { 0, 0 } }) {
                const float wx = x + lx * std::cos(a) + ly * std::sin(a), wy = y - lx * std::sin(a) + ly * std::cos(a);
                float h = 0;
                if (!tes->GetLandHeight({ wx, wy, 100000.0f }, h)) { land = false; break; }
                low = std::min(low, h); high = std::max(high, h);
                if (lx == 0 && ly == 0) centre = h;
            }
            if (!land) { out.push_back({{"land", false}}); continue; }
            int objects = 0;
            float biggest = 0;
            json what = json::array();
            for (const auto& b : blockers) {
                // Overlapping footprints, a little forgiving: a rock's bounds are a box around an uneven shape.
                if (std::hypot(b.at.x - x, b.at.y - y) < (radius + b.radius) * 0.8f) {
                    ++objects;
                    biggest = std::max(biggest, b.radius);
                    if (what.size() < 3) what.push_back(b.what);
                }
            }
            out.push_back({{"land", true}, {"ground", centre}, {"rise", high - low}, {"radius", radius}, {"objects", objects}, {"biggest", biggest}, {"what", what}});
        }
        return {{"spots", out}};
    }

    // FEAT-307 spike: a map marker for a home placed in game. The game's MapMarker object, placed at the home, given marker data
    // (name, shown and fast travel, type). The data's TESFullName has a vtable, so it starts as a copy of a known marker's.
    json AddMapMarker(const std::string& atRef, const std::string& name, int type) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* atForm = FormFromId(atRef);
        auto* at = atForm ? atForm->As<RE::TESObjectREFR>() : nullptr;
        auto* markerBase = RE::TESForm::LookupByID<RE::TESBoundObject>(0x00000010);  // MapMarker
        if (!player || !at || !markerBase) return {{"error", "Not a reference, or no MapMarker object: " + atRef}};
        RE::MapMarkerData* known = nullptr;
        for (auto& handle : player->GetPlayerRuntimeData().currentMapMarkers) {
            auto ref = handle.get();
            auto* extra = ref ? ref->extraList.GetByType<RE::ExtraMapMarker>() : nullptr;
            if (extra && extra->mapData) { known = extra->mapData; break; }
        }
        if (!known) return {{"error", "No map marker known to copy from (discover one first)"}};
        auto ref = player->PlaceObjectAtMe(markerBase, true);
        if (!ref) return {{"error", "PlaceObjectAtMe returned nothing"}};
        ref->SetPosition(at->GetPosition());
        auto* data = RE::malloc<RE::MapMarkerData>();
        std::memset(static_cast<void*>(data), 0, sizeof(RE::MapMarkerData));
        std::memcpy(static_cast<void*>(data), static_cast<const void*>(known), sizeof(void*));  // the TESFullName vtable only
        data->locationName.fullName = name;
        data->flags.set(RE::MapMarkerData::Flag::kVisible, RE::MapMarkerData::Flag::kCanTravelTo);
        data->type = static_cast<RE::MARKER_TYPE>(type);
        auto* extra = RE::BSExtraData::Create<RE::ExtraMapMarker>();
        extra->mapData = data;
        ref->extraList.Add(extra);
        player->GetPlayerRuntimeData().currentMapMarkers.push_back(ref->GetHandle());
        return {{"refId", std::format("{:08X}", ref->GetFormID())}, {"name", name}, {"type", type},
                {"note", "Spike: check the world map, then save and reload to see whether it stays."}};
    }

    // FEAT-307: the player's houses, for "where does this character live". A house is an interior cell whose location has the
    // LocTypePlayerHouse keyword (Breezehome, Hearthfire's, Creation Club's and player-home mods that use it); it is the player's when
    // the cell's owner is the player or the player's faction (the purchase scripts set that). Each comes with a reference inside to
    // send a resident to: a heading marker, else the inside of the front door (persistent, so known even while the house isn't loaded).
    // BUG-111 (Steam, 4,341 plugins): one entry per house, not per cell. A house's rooms are cells of one location, or of child
    // locations that are player houses too (a museum's wings): they roll up to the topmost one. Mods often give the player a house's
    // cells before it's bought (Hearthfire's, most home mods), so "owned" means marked as the player's, not bought.
    json OwnedHouses() {
        auto* data = RE::TESDataHandler::GetSingleton();
        auto* player = RE::PlayerCharacter::GetSingleton();
        auto* keyword = RE::TESForm::LookupByEditorID<RE::BGSKeyword>("LocTypePlayerHouse");
        if (!keyword) keyword = RE::TESForm::LookupByID<RE::BGSKeyword>(0x000FC1A3);
        if (!data || !player || !keyword) return {{"error", "The player-house keyword isn't in the game"}};
        auto* playerBase = player->GetActorBase();
        auto* playerFaction = RE::TESForm::LookupByID<RE::TESFaction>(0x00000DB1);
        struct House {
            RE::BGSLocation* loc = nullptr;
            RE::TESObjectCELL* cell = nullptr;
            RE::TESObjectREFR* inside = nullptr;
            bool marker = false, mine = false;
            int cells = 0;
        };
        std::vector<House> houses;
        std::unordered_map<RE::FormID, std::size_t> byLocation;
        for (auto* cell : data->interiorCells) {
            auto* loc = cell ? cell->GetLocation() : nullptr;
            if (!loc || !loc->HasKeyword(keyword)) continue;
            auto* top = loc;
            for (int depth = 0; depth < 8 && top->parentLoc && top->parentLoc->HasKeyword(keyword); ++depth) top = top->parentLoc;
            auto* owner = cell->GetOwner();
            const bool mine = owner && (owner == playerBase || owner == playerFaction);
            RE::TESObjectREFR* marker = nullptr;
            RE::TESObjectREFR* door = nullptr;
            cell->ForEachReference([&](RE::TESObjectREFR* ref) {
                auto* base = ref ? ref->GetBaseObject() : nullptr;
                if (!base || ref->IsDisabled()) return RE::BSContainer::ForEachResult::kContinue;
                if (base->GetFormID() == 0x00000034 && !marker) marker = ref;  // XMarkerHeading
                if (base->Is(RE::FormType::Door) && !door && ref->extraList.GetByType<RE::ExtraTeleport>()) door = ref;
                return RE::BSContainer::ForEachResult::kContinue;
            });
            auto [it, added] = byLocation.try_emplace(top->GetFormID(), houses.size());
            if (added) houses.push_back({top});
            auto& h = houses[it->second];
            ++h.cells;
            h.mine = h.mine || mine;
            // The room to send a resident to: one with a heading marker, else one with a door out.
            if ((marker && !h.marker) || (!h.inside && door)) {
                h.inside = marker ? marker : door;
                h.marker = marker != nullptr;
                h.cell = cell;
            }
            if (!h.cell) h.cell = cell;
        }
        json owned = json::array();
        int notOwned = 0;
        for (const auto& h : houses) {
            if (!h.mine) { ++notOwned; continue; }
            const char* name = h.loc->GetFullName();
            owned.push_back({{"name", name && *name ? name : h.cell->GetFullName()}, {"location", std::format("{:08X}", h.loc->GetFormID())},
                             {"cell", std::format("{:08X}", h.cell->GetFormID())}, {"rooms", h.cells},
                             {"ref", h.inside ? json(std::format("{:08X}", h.inside->GetFormID())) : json(nullptr)}});
        }
        return {{"owned", owned}, {"notOwned", notOwned},
                {"note", "Marked as the player's in the game. Many mods (and Hearthfire) mark a house as the player's before it's bought: ask the player which are really theirs."}};
    }

    json RemoveObject(const std::string& refId) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* ref = form ? form->As<RE::TESObjectREFR>() : nullptr;
        if (!ref || (ref->GetFormID() >> 24) != 0xFF || ref->Is(RE::FormType::ActorCharacter)) return {{"error", "Only objects placed at run time can be removed: " + refId}};
        // A map marker is also on the player's list (add_map_marker): off it first, or the list keeps a dead handle.
        if (auto* player = RE::PlayerCharacter::GetSingleton()) {
            auto& markers = player->GetPlayerRuntimeData().currentMapMarkers;
            for (std::uint32_t i = markers.size(); i-- > 0;) {
                if (markers[i].get().get() == ref) markers.erase(markers.begin() + i);
            }
        }
        ref->Disable();
        ref->SetDelete(true);
        return {{"refId", refId}, {"removed", true}};
    }

    json DiscoverAllMapMarkers() {
        // tmm 1 is kept as console command — native approach requires iterating all
        // TESObjectREFR in every worldspace to find ExtraMapMarker data, which is
        // significantly more expensive than the engine's built-in tmm command.
        Helpers::ExecuteConsoleCommand("tmm 1");
        return {{"discovered", true}, {"message", "All map markers discovered"}};
    }

    json GetGameTime() {
        auto* calendar = RE::Calendar::GetSingleton();
        if (!calendar) return {{"error", "Calendar not available"}};

        json result;
        result["gameHour"] = calendar->GetHour();
        result["daysPassed"] = calendar->GetDaysPassed();
        result["day"] = calendar->GetDay();
        result["month"] = calendar->GetMonth();
        result["year"] = calendar->GetYear();
        result["dayOfWeek"] = calendar->GetDayOfWeek();
        return result;
    }

    json SetGameTime(float hours) {
        auto* calendar = RE::Calendar::GetSingleton();
        if (!calendar) return {{"error", "Calendar not available"}};

        // gameHour is a TESGlobal — set its value directly
        if (calendar->gameHour) {
            calendar->gameHour->value = hours;
            return {{"set", true}, {"gameHour", hours}};
        }
        return {{"error", "Game hour global not available"}};
    }

    json ToggleCollision(const std::string& refFormIdHex) {
        RE::TESObjectREFR* target = nullptr;

        if (refFormIdHex.empty() || refFormIdHex == "player") {
            target = RE::PlayerCharacter::GetSingleton();
        } else {
            try {
                auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
                if (form) target = form->As<RE::TESObjectREFR>();
            } catch (...) {}
        }

        if (!target) return {{"error", "Target not found"}};

        // Uses the console's tcl, and reports the state from the game's own reply
        // ("... -> On" / "... -> Off"), so the tool always agrees with the console.
        // - Player: tcl with nothing selected, the player's no-clip. The reference flag
        //   that TESObjectREFR::SetCollision (and `player.tcl`) sets does not stop the
        //   player's collision, and the game clears it again: tested on 1.7.104, every
        //   call read "on" and the player still could not pass through walls.
        // - Anything else: tcl with that reference selected.
        // No remembered state: it would go wrong after a typed tcl, a reload of this
        // DLL, or a new game session.
        const bool isPlayer = target == RE::PlayerCharacter::GetSingleton();
        auto result = Helpers::ExecuteConsoleCommandOn("tcl", isPlayer ? nullptr : target);
        if (result.contains("error")) return result;

        const std::string output = result.value("output", "");
        json enabled = nullptr;  // null: the game's reply was missing or not understood
        if (auto arrow = output.rfind("->"); arrow != std::string::npos) {
            std::string state;
            for (char c : output.substr(arrow + 2)) {
                if (std::isalpha(static_cast<unsigned char>(c))) {
                    state += static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
                }
            }
            if (state == "on") enabled = true;
            if (state == "off") enabled = false;
        }

        return {{"collisionEnabled", enabled},
                {"consoleOutput", output},
                {"target", target->GetName() ? target->GetName() : ""},
                {"refId", std::format("{:08X}", target->GetFormID())}};
    }

    // ==================== Economy (Phase 7) ====================

    json GetNearbyMerchants(float radius) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* processLists = RE::ProcessLists::GetSingleton();
        if (!processLists) return {{"error", "Process lists not available"}};

        auto playerPos = player->GetPosition();
        json merchants = json::array();

        for (auto& actorHandle : processLists->highActorHandles) {
            auto actorPtr = actorHandle.get();
            if (!actorPtr) continue;

            auto* actor = actorPtr.get();
            if (!actor || actor == player || actor->IsDead()) continue;

            auto actorPos = actor->GetPosition();
            float dx = actorPos.x - playerPos.x;
            float dy = actorPos.y - playerPos.y;
            float dz = actorPos.z - playerPos.z;
            float distance = std::sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > radius) continue;

            // Check if this actor is a merchant via their factions
            bool isMerchant = false;
            std::string merchantType;

            actor->VisitFactions([&](RE::TESFaction* faction, std::int8_t) -> bool {
                if (faction && faction->IsVendor()) {
                    isMerchant = true;
                    merchantType = faction->GetName() ? faction->GetName() : "";
                }
                return true;
            });

            if (!isMerchant) continue;

            auto* base = actor->GetActorBase();
            json m;
            m["refId"] = std::format("{:08X}", actor->GetFormID());
            m["baseId"] = base ? std::format("{:08X}", base->GetFormID()) : "";
            m["name"] = actor->GetName();
            m["merchantFaction"] = merchantType;
            m["distance"] = distance;

            merchants.push_back(m);
        }

        return {{"merchants", merchants}, {"count", merchants.size()}};
    }

    json GetMerchantInventory(const std::string& refFormIdHex) {
        try {
            auto* form = RE::TESForm::LookupByID(Helpers::ParseFormId(refFormIdHex));
            if (!form) return {{"error", "Reference not found: " + refFormIdHex}};

            auto* actor = form->As<RE::Actor>();
            if (!actor) return {{"error", "Not an actor: " + refFormIdHex}};

            // Get the merchant's inventory (what they carry for sale)
            auto inv = actor->GetInventory();
            json items = json::array();

            for (auto& [itemForm, data] : inv) {
                if (!itemForm || data.first <= 0) continue;
                try {
                    json item;
                    item["formId"] = std::format("{:08X}", itemForm->GetFormID());
                    item["name"] = itemForm->GetName();
                    item["count"] = data.first;

                    if (auto* boundObj = itemForm->As<RE::TESBoundObject>()) {
                        item["value"] = boundObj->GetGoldValue();
                        item["weight"] = boundObj->GetWeight();
                    }

                    switch (itemForm->GetFormType()) {
                        case RE::FormType::Weapon: item["type"] = "weapon"; break;
                        case RE::FormType::Armor: item["type"] = "armor"; break;
                        case RE::FormType::AlchemyItem: item["type"] = "potion"; break;
                        case RE::FormType::Ingredient: item["type"] = "ingredient"; break;
                        case RE::FormType::Book: item["type"] = "book"; break;
                        case RE::FormType::Misc: item["type"] = "misc"; break;
                        default: item["type"] = "other"; break;
                    }

                    items.push_back(item);
                } catch (...) { continue; }
            }

            return {{"refId", refFormIdHex},
                    {"name", actor->GetName() ? actor->GetName() : ""},
                    {"items", items}, {"count", items.size()}};
        } catch (...) {
            return {{"error", "Failed to get merchant inventory: " + refFormIdHex}};
        }
    }

    json GetBounties() {
        auto* player = RE::PlayerCharacter::GetSingleton();
        if (!player) return {{"error", "Player not available"}};

        auto* dataHandler = RE::TESDataHandler::GetSingleton();
        if (!dataHandler) return {{"error", "Data handler not available"}};

        json bounties = json::array();

        for (auto* faction : dataHandler->GetFormArray<RE::TESFaction>()) {
            if (!faction) continue;
            try {
                auto gold = player->GetCrimeGoldValue(faction);
                if (gold > 0) {
                    json b;
                    b["faction"] = faction->GetName() ? faction->GetName() : "";
                    b["factionFormId"] = std::format("{:08X}", faction->GetFormID());
                    b["bounty"] = gold;
                    bounties.push_back(b);
                }
            } catch (...) { continue; }
        }

        return {{"bounties", bounties}, {"totalBounties", bounties.size()}};
    }

    json ClearBounty(const std::string& factionFormIdHex) {
        try {
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};

            auto* faction = RE::TESForm::LookupByID<RE::TESFaction>(Helpers::ParseFormId(factionFormIdHex));
            if (!faction) return {{"error", "Faction not found: " + factionFormIdHex}};

            auto oldBounty = player->GetCrimeGoldValue(faction);
            player->SetCrimeGoldValue(faction, false, 0);
            player->SetCrimeGoldValue(faction, true, 0);

            return {{"cleared", true},
                    {"faction", faction->GetName() ? faction->GetName() : ""},
                    {"oldBounty", oldBounty}};
        } catch (...) {
            return {{"error", "Failed to clear bounty: " + factionFormIdHex}};
        }
    }

    json PlayMusic(const std::string& formIdHex) {
        try {
            auto* musicType = RE::TESForm::LookupByID<RE::BGSMusicType>(Helpers::ParseFormId(formIdHex));
            if (!musicType) return {{"error", "Music type not found: " + formIdHex}};

            auto* musicManager = RE::BSMusicManager::GetSingleton();
            if (!musicManager) return {{"error", "Music manager not available"}};

            musicType->DoPlay();

            return {
                {"playing", true},
                {"formId", formIdHex},
                {"name", musicType->GetFormEditorID() ? musicType->GetFormEditorID() : ""},
                {"priority", musicType->priority},
                {"fadeTime", musicType->fadeTime}
            };
        } catch (...) {
            return {{"error", "Failed to play music: " + formIdHex}};
        }
    }

    json StopMusic() {
        auto* musicManager = RE::BSMusicManager::GetSingleton();
        if (!musicManager) return {{"error", "Music manager not available"}};

        auto* current = musicManager->current;
        if (!current) return {{"stopped", false}, {"message", "No music currently playing"}};

        // BGSMusicType inherits from both TESForm and BSIMusicType.
        // dynamic_cast fails across the MI boundary. Use reinterpret offset instead:
        // BGSMusicType layout: TESForm (0x00-0x1F) + BSIMusicType (0x20-0x6F) + editorID (0x70)
        // BSIMusicType* points to offset 0x20, so BGSMusicType* = BSIMusicType* - 0x20
        std::string name;
        auto* bgsMusicType = reinterpret_cast<RE::BGSMusicType*>(
            reinterpret_cast<std::uintptr_t>(current) - 0x20);
        // Validate by checking if it's actually a MusicType form
        if (bgsMusicType && bgsMusicType->GetFormType() == RE::FormType::MusicType) {
            name = bgsMusicType->GetFormEditorID() ? bgsMusicType->GetFormEditorID() : "";
        }

        current->DoFinish(true);

        json result;
        result["stopped"] = true;
        result["wasPlaying"] = name;
        return result;
    }

}

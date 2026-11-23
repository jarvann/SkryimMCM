#include "WorldManager.h"
#include "Helpers.h"
#include "TaskQueue.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <format>
#include <unordered_map>
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

    json PlaceObject(const std::string& baseId, float x, float y, float z, float angleZ, bool onGround) {
        auto* player = RE::PlayerCharacter::GetSingleton();
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(baseId)); } catch (...) {}
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
            for (const auto& [lx, ly] : std::initializer_list<std::pair<float, float>>{ { lo.x, lo.y }, { lo.x, hi.y }, { hi.x, lo.y }, { hi.x, hi.y }, { 0, 0 } }) {
                const float wx = x + lx * std::cos(a) + ly * std::sin(a), wy = y - lx * std::sin(a) + ly * std::cos(a);
                float h = 0;
                if (tes->GetLandHeight({ wx, wy, z + 5000.0f }, h)) { low = std::min(low, h); high = std::max(high, h); }
            }
            if (low > 1e8f) low = high = z;
            float centre = z; tes->GetLandHeight({ x, y, z + 5000.0f }, centre);
            z = span > 300.0f ? low - std::min(40.0f, 0.1f * (high - low) + 10.0f) : centre;
        }
        ref->SetPosition(x, y, z);
        return {{"refId", std::format("{:08X}", ref->GetFormID())}, {"base", std::format("{:08X}", base->GetFormID())}, {"x", x}, {"y", y}, {"z", z},
                {"footprint", span}, {"groundRise", high - low}};
    }

    json RemoveObject(const std::string& refId) {
        RE::TESForm* form = nullptr;
        try { form = RE::TESForm::LookupByID(Helpers::ParseFormId(refId)); } catch (...) {}
        auto* ref = form ? form->As<RE::TESObjectREFR>() : nullptr;
        if (!ref || (ref->GetFormID() >> 24) != 0xFF || ref->Is(RE::FormType::ActorCharacter)) return {{"error", "Only objects placed at run time can be removed: " + refId}};
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

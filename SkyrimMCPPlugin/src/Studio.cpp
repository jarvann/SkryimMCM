#include "Studio.h"
#include "Helpers.h"
#include "FaceCamera.h"
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>
#include <algorithm>
#include <cmath>
#include <format>
#include <numbers>
#include <string>
#include <string_view>
#include <vector>

namespace SkyrimMCP {

    namespace {
        // Studio lights are found again by this name prefix (the logic DLL can be reloaded between calls).
        constexpr std::string_view kLightPrefix = "SkyLinkStudio_";
        RE::FormID s_marker = 0;  // the marked spot, so a caller that lost the Go reply (a loading-screen timeout) can find it

        struct LightSpec {
            const char* name;
            float yawDeg;   // around the head: 0 = in front of the face, + = the character's right
            float forward;  // distance from the head
            float up;       // above the head node
            RE::NiColor color;
            float fade;     // brightness
        };

        constexpr float kRadius = 260.0f;

        std::vector<LightSpec> Preset(const std::string& name) {
            const RE::NiColor white{ 1.0f, 1.0f, 1.0f };
            // neutral is even on both sides (for measuring: a strong key light tripped the one-sided-light warning);
            // portrait is the classic strong key and soft fill.
            if (name == "neutral") return { { "key", 40, 65, 15, white, 1.2f }, { "fill", -40, 65, 15, white, 1.1f }, { "rim", 180, 60, 35, white, 0.4f } };
            if (name == "portrait") return { { "key", 40, 60, 15, white, 1.6f }, { "fill", -40, 70, 0, white, 0.7f }, { "rim", 180, 60, 35, white, 0.5f } };
            // warm and daylight are even on both sides too (BUG-040: warm tripped the one-sided-light warning).
            if (name == "warm") return { { "key", 40, 65, 15, { 1.0f, 0.85f, 0.68f }, 1.2f }, { "fill", -40, 65, 15, { 1.0f, 0.88f, 0.75f }, 1.1f }, { "rim", 180, 60, 35, { 1.0f, 0.85f, 0.7f }, 0.4f } };
            if (name == "daylight") return { { "key", 40, 65, 25, { 1.0f, 0.97f, 0.9f }, 1.3f }, { "fill", -40, 65, 15, { 0.85f, 0.9f, 1.0f }, 1.1f }, { "rim", 180, 60, 35, white, 0.5f } };
            if (name == "soft") return { { "key", 35, 70, 10, white, 1.1f }, { "fill", -35, 70, 10, white, 1.1f }, { "rim", 180, 60, 35, white, 0.3f } };
            return {};
        }

        RE::NiNode* Root(RE::Actor* a_actor) {
            auto* root = a_actor ? a_actor->Get3D(false) : nullptr;
            return root ? root->AsNode() : nullptr;
        }

        RE::ShadowSceneNode* SceneLights() { return RE::BSShaderManager::State::GetSingleton().shadowSceneNode[0]; }

        int RemoveLights(RE::NiNode* a_root) {
            if (!a_root) return 0;
            std::vector<RE::NiAVObject*> found;
            for (auto& child : a_root->GetChildren()) {
                if (child && std::string_view(child->name.c_str()).starts_with(kLightPrefix)) found.push_back(child.get());
            }
            auto* scene = SceneLights();
            for (auto* object : found) {
                if (auto* light = netimmerse_cast<RE::NiLight*>(object); light && scene) scene->RemoveLight(light);
                a_root->DetachChild2(object);
            }
            return static_cast<int>(found.size());
        }

        RE::NiPoint3 ToLocal(RE::NiAVObject* a_root, const RE::NiPoint3& a_world) {
            const auto d = a_world - a_root->world.translate;
            const auto& r = a_root->world.rotate;
            // world = R * local * scale  =>  local = R^T * d / scale  (columns of R are the local axes)
            RE::NiPoint3 local{ r.entry[0][0] * d.x + r.entry[1][0] * d.y + r.entry[2][0] * d.z,
                                r.entry[0][1] * d.x + r.entry[1][1] * d.y + r.entry[2][1] * d.z,
                                r.entry[0][2] * d.x + r.entry[1][2] * d.y + r.entry[2][2] * d.z };
            const float scale = a_root->world.scale > 0.0f ? a_root->world.scale : 1.0f;
            return local / scale;
        }
    }

    namespace Studio {

        json Lights(const std::string& preset, float intensity) {
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* root = Root(player);
            if (!root) return {{"error", "The player's 3D isn't loaded"}};
            auto* scene = SceneLights();
            if (!scene) return {{"error", "The renderer's light list isn't available"}};

            const int removed = RemoveLights(root);
            if (preset == "off" || preset.empty()) return {{"removed", removed}, {"preset", "off"}};

            const auto specs = Preset(preset);
            if (specs.empty()) return {{"error", "preset must be neutral, portrait, warm, daylight, soft or off"}};
            auto* head = root->GetObjectByName(RE::BSFixedString("NPC Head [Head]"));
            if (!head) return {{"error", "The player's head isn't loaded"}};

            // Light positions are worked out in world space from the way the body faces, then stored in the
            // root's own space, so the lights stay put relative to her.
            constexpr float pi = std::numbers::pi_v<float>;
            const float facing = player->GetAngleZ();
            json placed = json::array();
            for (const auto& spec : specs) {
                const float around = facing + spec.yawDeg * pi / 180.0f;
                const RE::NiPoint3 world = head->world.translate +
                    RE::NiPoint3{ std::sin(around) * spec.forward, std::cos(around) * spec.forward, spec.up };

                RE::NiPointer<RE::NiPointLight> light{ RE::NiPointLight::Create() };
                if (!light) return {{"error", "Couldn't create a light"}};
                // The preset is in the name, so Status reads it from the lights themselves (BUG-039: the preset kept
                // in a variable came back empty after face edits while the lights were still there).
                light->name = std::format("{}{}_{}", kLightPrefix, preset, spec.name);
                auto& data = light->GetLightRuntimeData();
                data.ambient = RE::NiColor{ 0.0f, 0.0f, 0.0f };
                data.diffuse = spec.color;
                data.radius = RE::NiPoint3{ kRadius, kRadius, kRadius };
                data.fade = spec.fade * std::clamp(intensity, 0.1f, 4.0f);
                light->SetLightAttenuation(kRadius);
                light->local.translate = ToLocal(root, world);
                root->AttachChild(light.get(), true);
                RE::NiUpdateData update{};
                light->Update(update);

                RE::ShadowSceneNode::LIGHT_CREATE_PARAMS params{};
                params.dynamic = true;
                params.shadowLight = false;
                params.portalStrict = false;
                params.affectLand = true;
                params.affectWater = true;
                params.neverFades = true;
                params.fov = pi;
                params.falloff = 1.0f;
                params.nearDistance = 5.0f;
                params.depthBias = 0.0f;
                params.sceneGraphIndex = 0;
                params.restrictedNode = nullptr;
                params.lensFlareData = nullptr;
                scene->AddLight(light.get(), params);

                placed.push_back({{"light", spec.name}, {"world", {world.x, world.y, world.z}}, {"fade", data.fade},
                                  {"color", {spec.color.red, spec.color.green, spec.color.blue}}});
            }
            return {{"preset", preset}, {"intensity", intensity}, {"removed", removed}, {"lights", placed}};
        }

        // A readable name for where the player is: the cell's name, else (outdoors, where cells are unnamed) the location and
        // worldspace, e.g. "Riverwood (Skyrim)".
        json CellName(RE::PlayerCharacter* player, RE::TESObjectCELL* cell) {
            if (cell && cell->GetName() && cell->GetName()[0]) return cell->GetName();
            std::string name;
            if (auto* location = player ? player->GetCurrentLocation() : nullptr; location && location->GetName()) name = location->GetName();
            if (auto* world = player ? player->GetWorldspace() : nullptr; world && world->GetName() && world->GetName()[0]) {
                // Outside any named location: "Wilderness (Skyrim)" rather than the bare worldspace.
                name = (name.empty() ? std::string("Wilderness") : name) + " (" + world->GetName() + ")";
            }
            return name.empty() ? json(nullptr) : json(name);
        }

        json Go(const std::string& cellEditorId) {
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};
            if (auto* ui = RE::UI::GetSingleton(); ui && ui->GameIsPaused()) {
                return {{"error", "A menu is open (the map, inventory, a container, crafting or another pausing menu): close it and try again"}};
            }
            if (player->IsInCombat()) return {{"error", "In combat: the studio has to wait"}};
            auto* xmarker = RE::TESForm::LookupByID<RE::TESBoundObject>(0x0000003B);
            if (!xmarker) return {{"error", "XMarker (Skyrim.esm 00003B) not found"}};

            auto marker = player->PlaceObjectAtMe(xmarker, true);
            if (!marker) return {{"error", "Couldn't place the marker"}};
            marker->SetPosition(player->GetPosition());
            marker->data.angle = player->data.angle;  // MoveTo back matches the marker's rotation

            RemoveLights(Root(player));
            const json dispelled = FaceCamera::LightEffects(true);  // BUG-052: the studio is about controlled light
            const char* cellName = player->GetParentCell() ? player->GetParentCell()->GetFormEditorID() : nullptr;
            s_marker = marker->GetFormID();
            json result = {{"markerId", std::format("{:08X}", marker->GetFormID())},
                           {"markedCell", cellName ? cellName : ""},
                           {"markedPosition", {player->GetPosition().x, player->GetPosition().y, player->GetPosition().z}},
                           {"markedFacingDeg", player->GetAngleZ() * 180.0f / std::numbers::pi_v<float>}};
            if (!player->CenterOnCell(cellEditorId.c_str())) {
                marker->Disable();
                marker->SetDelete(true);
                s_marker = 0;
                return {{"error", std::format("No cell with editor ID '{}' (the move didn't start; nothing was changed)", cellEditorId)}};
            }
            result["going"] = cellEditorId;
            result["lightSpellsDispelled"] = dispelled;  // always present (BUG-058), empty when none were on
            return result;
        }

        json Return(RE::FormID markerId) {
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (auto* ui = RE::UI::GetSingleton(); ui && ui->GameIsPaused()) {
                return {{"error", "A menu is open (the map, inventory, a container, crafting or another pausing menu): close it and try again"}};
            }
            auto* marker = RE::TESForm::LookupByID<RE::TESObjectREFR>(markerId);
            if (!player || !marker) return {{"error", std::format("Marker {:08X} not found", markerId)}};
            const int removed = RemoveLights(Root(player));
            player->MoveTo(marker);
            return {{"returning", std::format("{:08X}", markerId)}, {"lightsRemoved", removed}};
        }

        json Cleanup(RE::FormID markerId) {
            auto* marker = RE::TESForm::LookupByID<RE::TESObjectREFR>(markerId);
            if (!marker) {
                if (s_marker == markerId) s_marker = 0;
                return {{"deleted", false}, {"note", "marker already gone"}};
            }
            marker->Disable();
            marker->SetDelete(true);
            if (s_marker == markerId) s_marker = 0;
            return {{"deleted", true}};
        }

        json FindMarkers() {
            // Markers placed at run time (FF-prefixed references to XMarker), newest first: a studio trip whose Go reply
            // was lost left one at the starting spot (BUG-038).
            std::vector<RE::TESObjectREFR*> found;
            {
                const auto& [all, lock] = RE::TESForm::GetAllForms();
                RE::BSReadLockGuard guard(lock);
                if (all) {
                    for (auto& [id, form] : *all) {
                        if (!form || (id >> 24) != 0xFF || form->GetFormType() != RE::FormType::Reference) continue;
                        auto* ref = static_cast<RE::TESObjectREFR*>(form);
                        auto* base = ref->GetBaseObject();
                        if (base && base->GetFormID() == 0x0000003B && !ref->IsDeleted() && !ref->IsDisabled()) found.push_back(ref);
                    }
                }
            }
            std::sort(found.begin(), found.end(), [](auto* a, auto* b) { return a->GetFormID() > b->GetFormID(); });
            json list = json::array();
            for (std::size_t i = 0; i < found.size() && i < 20; i++) {  // BUG-074: 5 hid the Teleport('back') marker
                auto* ref = found[i];
                auto* cell = ref->GetParentCell();
                const char* cellId = cell ? cell->GetFormEditorID() : nullptr;
                auto* space = ref->GetWorldspace();
                const char* spaceId = space ? space->GetFormEditorID() : nullptr;
                const auto pos = ref->GetPosition();
                list.push_back({{"markerId", std::format("{:08X}", ref->GetFormID())},
                                {"cell", cellId ? cellId : ""},
                                {"worldspace", spaceId ? spaceId : ""},
                                {"position", {pos.x, pos.y, pos.z}},
                                {"facingDeg", ref->GetAngleZ() * 180.0f / std::numbers::pi_v<float>}});
            }
            return {{"markers", list}, {"total", found.size()}};
        }

        json Status() {
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* root = Root(player);
            int lights = 0;
            std::string preset;
            if (root) {
                for (auto& child : root->GetChildren()) {
                    const std::string_view name = child ? std::string_view(child->name.c_str()) : std::string_view{};
                    if (!name.starts_with(kLightPrefix)) continue;
                    ++lights;
                    const auto rest = name.substr(kLightPrefix.size());
                    if (const auto cut = rest.find('_'); cut != std::string_view::npos) preset = std::string(rest.substr(0, cut));
                }
            }
            auto* cell = player ? player->GetParentCell() : nullptr;
            const char* cellId = cell ? cell->GetFormEditorID() : nullptr;
            return {{"lights", lights},
                    {"preset", lights > 0 && !preset.empty() ? json(preset) : json(nullptr)},
                    {"cell", cellId ? cellId : ""},
                    {"cellFormId", cell ? json(std::format("{:08X}", cell->GetFormID())) : json(nullptr)},
                    {"cellName", CellName(player, cell)},
                    {"interior", cell && cell->IsInteriorCell()},
                    {"markerId", s_marker ? json(std::format("{:08X}", s_marker)) : json(nullptr)}};
        }
    }
}

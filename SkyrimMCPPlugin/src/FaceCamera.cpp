#include "FaceCamera.h"
#include "Helpers.h"
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>
#include <algorithm>
#include <cctype>
#include <cmath>
#include <format>
#include <numbers>
#include <string>
#include <string_view>
#include <vector>

namespace SkyrimMCP {

    namespace {
        // What Enter changed, so Exit can put it back. Names, not pointers: the 3D can be rebuilt between
        // calls, and this DLL can be reloaded.
        bool s_active = false;
        bool s_wasFreeCamera = false;
        bool s_wasFrozen = false;
        bool s_hidHud = false;
        bool s_expressionSet = false;
        RE::NiPoint3 s_aimedAt{};      // where Aim put the camera, for Check (BUG-056)
        bool s_aimed = false;  // Prepare set an expression: Enter keeps it, Exit clears it
        std::vector<std::string> s_hiddenNodes;
        std::vector<RE::NiPointer<RE::NiAVObject>> s_hiddenEffects;  // particles culled for this capture only

        RE::NiAVObject* HeadNode(RE::Actor* a_actor) {
            auto* root = a_actor ? a_actor->Get3D(false) : nullptr;
            return root ? root->GetObjectByName(RE::BSFixedString("NPC Head [Head]")) : nullptr;
        }

        // Rotates v by the unit quaternion q (v' = q v q*).
        RE::NiPoint3 Rotate(const RE::NiQuaternion& q, const RE::NiPoint3& v) {
            const RE::NiPoint3 u{ q.x, q.y, q.z };
            const RE::NiPoint3 t = u.Cross(v) * 2.0f;
            return v + t * q.w + u.Cross(t);
        }

        // How well the free camera looks along 'wanted': the camera looks along +Y in its own space.
        // The quaternion's handedness isn't documented, so both directions are tried.
        float Alignment(RE::FreeCameraState* a_state, const RE::NiPoint3& a_wanted) {
            RE::NiQuaternion q;
            a_state->GetRotation(q);
            const RE::NiPoint3 forward{ 0.0f, 1.0f, 0.0f };
            const auto f1 = Rotate(q, forward);
            const auto f2 = Rotate(RE::NiQuaternion(q.w, -q.x, -q.y, -q.z), forward);
            return std::max(f1.Dot(a_wanted), f2.Dot(a_wanted));
        }

        // The heading (Skyrim convention: 0 = +Y, clockwise) the head faces: the head bone's local axis that
        // lies closest to the body's facing, flattened. Returns false when no axis is close (head turned
        // over ~60 degrees), so the caller can fall back to the body.
        bool HeadHeading(RE::NiAVObject* a_head, float a_bodyHeading, float& a_out) {
            const RE::NiPoint3 body{ std::sin(a_bodyHeading), std::cos(a_bodyHeading), 0.0f };
            const RE::NiPoint3 axes[3] = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            float best = -2.0f;
            RE::NiPoint3 bestDir;
            for (const auto& axis : axes) {
                for (float sign : { 1.0f, -1.0f }) {
                    RE::NiPoint3 dir = (a_head->world.rotate * axis) * sign;  // local axis in world space
                    dir.z = 0.0f;
                    if (dir.Length() < 0.3f) continue;  // mostly vertical
                    dir.Unitize();
                    if (const float d = dir.Dot(body); d > best) {
                        best = d;
                        bestDir = dir;
                    }
                }
            }
            if (best < 0.5f) return false;
            a_out = std::atan2(bestDir.x, bestDir.y);
            return true;
        }

        void HideHair(RE::Actor* a_actor) {
            auto* npc = a_actor->GetActorBase();
            auto* root = a_actor->Get3D(false);
            if (!npc || !root || !npc->headParts) return;
            auto hide = [&](RE::BGSHeadPart* a_part) {
                const char* name = a_part ? a_part->GetFormEditorID() : nullptr;
                if (!name || !name[0]) return;
                if (auto* node = root->GetObjectByName(RE::BSFixedString(name)); node && !node->GetAppCulled()) {
                    node->SetAppCulled(true);
                    s_hiddenNodes.emplace_back(name);
                }
            };
            for (int i = 0; i < npc->numHeadParts; i++) {
                auto* part = npc->headParts[i];
                if (!part || part->type.get() != RE::BGSHeadPart::HeadPartType::kHair) continue;
                hide(part);
                for (auto* extra : part->extraParts) hide(extra);
            }
        }
    }

    namespace {
        // Breath vapour and other particle effects on the character (FEAT-242): cold-weather breath drifts across
        // the mouth and skews the lip and smile measurements. Culls particle systems and nodes named like breath
        // in the player's 3D; Exit shows them again. Held by pointer: only for the seconds of one capture.
        int HideEffects(RE::Actor* a_actor) {
            auto* root = a_actor ? a_actor->Get3D(false) : nullptr;
            if (!root) return 0;
            int hidden = 0;
            RE::BSVisit::TraverseScenegraphObjects(root, [&](RE::NiAVObject* a_object) {
                if (!a_object || a_object->GetAppCulled()) return RE::BSVisit::BSVisitControl::kContinue;
                std::string name = a_object->name.c_str() ? a_object->name.c_str() : "";
                std::transform(name.begin(), name.end(), name.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
                const auto* rtti = a_object->GetRTTI();
                const std::string_view type = rtti && rtti->GetName() ? rtti->GetName() : "";
                if (name.find("breath") != std::string::npos || type.find("ParticleSystem") != std::string_view::npos) {
                    a_object->SetAppCulled(true);
                    s_hiddenEffects.emplace_back(a_object);
                    ++hidden;
                }
                return RE::BSVisit::BSVisitControl::kContinue;
            });
            return hidden;
        }
    }

    namespace FaceCamera {
        json LightEffects(bool dispel) {
            // BUG-052: a light spell on the player (Candlelight) washes out the face in captures and adds to the studio
            // lights (skin lightness about 72 with it, 63 without). Lists the active light effects; dispels them if asked.
            json found = json::array();
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* target = player ? player->AsMagicTarget() : nullptr;
            auto* list = target ? target->GetActiveEffectList() : nullptr;
            if (!list) return found;
            std::vector<RE::ActiveEffect*> lights;
            for (auto* effect : *list) {
                auto* base = effect ? effect->GetBaseObject() : nullptr;
                if (!base || base->GetArchetype() != RE::EffectSetting::Archetype::kLight) continue;
                if (effect->flags.any(RE::ActiveEffect::Flag::kInactive, RE::ActiveEffect::Flag::kDispelled)) continue;
                found.push_back(base->GetName() ? base->GetName() : "light effect");
                lights.push_back(effect);
            }
            if (dispel) for (auto* effect : lights) effect->Dispel(true);
            return found;
        }



        json Prepare(int expression, float strength, float mouthOpen) {
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};
            // A walking character turns and bobs between frames, so the shot misses the face (FEAT-241).
            // The caller waits; only the player can stop.
            RE::NiPoint3 velocity;
            player->GetLinearVelocity(velocity);
            if (const float speed = velocity.Length(); speed > 5.0f || player->IsMoving()) {
                return {{"moving", true}, {"speed", speed}};
            }
            // Forcing the default idle with a weapon or spell out leaves the hands stuck readied (BUG-029):
            // sheathe first and let the caller wait for the animation, then call Prepare again.
            if (auto* state = player->AsActorState(); state && state->IsWeaponDrawn()) {
                if (state->GetWeaponState() == RE::WEAPON_STATE::kDrawn) player->DrawWeaponMagicHands(false);
                return {{"sheathing", true}};
            }
            const bool idleReset = player->NotifyAnimationGraph(RE::BSFixedString("IdleForceDefaultState"));
            // FEAT-243: let go of whatever the head is tracking (a passer-by, a sound), so it turns back to face
            // forward during the settle wait instead of being caught mid-turn.
            int headTrackCleared = 0;
            if (auto* process = player->GetActorRuntimeData().currentProcess; process && process->high) {
                using Type = RE::HighProcessData::HEAD_TRACK_TYPES::HEAD_TRACK_TYPE;
                for (std::uint32_t t = 0; t < Type::kTotal; t++) {
                    if (process->high->headTrackTarget[t]) {
                        process->high->ClearHeadtrackTarget(static_cast<Type>(t), false);
                        ++headTrackCleared;
                    }
                }
            }
            bool expressionReset = false;
            s_expressionSet = false;
            if (auto* face = player->GetFaceGenAnimationData()) {
                face->ClearExpressionOverride();
                face->Reset(0.0f, true, true, true, false);
                expressionReset = true;
                // FEAT-240: an expression to compare with a smiling (or otherwise not neutral) reference.
                // Set before the caller's settle wait so it has blended in when time stops.
                if (expression >= 0 && expression <= 16) {
                    face->SetExpressionOverride(static_cast<std::uint32_t>(expression), std::clamp(strength, 0.0f, 1.0f));
                    s_expressionSet = true;
                }
                // BUG-043: the game smile keeps the lips closed; a photo often shows an open-mouthed smile. Phoneme 1
                // ("BigAah", as the mfg console command numbers them) opens the jaw.
                if (mouthOpen > 0.0f) {
                    face->phenomeKeyFrame.SetValue(1, std::clamp(mouthOpen, 0.0f, 1.0f));
                    s_expressionSet = true;
                }
            }
            return {{"idleReset", idleReset}, {"expressionReset", expressionReset}, {"expression", s_expressionSet ? expression : -1},
                    {"headTrackCleared", headTrackCleared}, {"lightEffects", LightEffects(false)}};
        }

        json Enter(bool hideHud, bool hideHair, bool hideEffects) {
            auto* camera = RE::PlayerCamera::GetSingleton();
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* main = RE::Main::GetSingleton();
            if (!camera || !player || !main) return {{"error", "Camera or player not available"}};
            if (!HeadNode(player)) return {{"error", "The player's head isn't loaded (third-person 3D missing)"}};
            // BUG-057: with the map (or another pausing menu) open the screenshot is of the menu, and the camera check
            // still passes, since the camera itself is in place.
            if (auto* ui = RE::UI::GetSingleton(); ui && ui->GameIsPaused()) {
                return {{"error", "A menu is open (the map, inventory, journal or another pausing menu): close it and capture again"}};
            }

            // Don't freeze mid-blink (FEAT-239): a half-closed lid skews the eye measurements. The caller
            // waits a moment and calls Enter again; a blink lasts a fraction of a second.
            if (!s_active) {
                if (auto* face = player->GetFaceGenAnimationData()) {
                    using Stage = RE::BSFaceGenAnimationData::EyesBlinkingStage;
                    const auto stage = face->eyesBlinkingStage;
                    if (stage == Stage::BlinkDown || stage == Stage::BlinkUp || stage == Stage::BlinkDownAndWait1 ||
                        stage == Stage::BlinkDownAndWait2) {
                        return {{"blinking", true}, {"stage", static_cast<int>(stage)}};
                    }
                }
            }

            if (!s_active) {
                s_wasFreeCamera = camera->IsInFreeCameraMode();
                s_wasFrozen = main->GetRuntimeData().freezeTime;
                s_hidHud = false;
                s_hiddenNodes.clear();
            }
            if (!camera->IsInFreeCameraMode()) camera->ToggleFreeCameraMode(true);

            auto* ui = RE::UI::GetSingleton();
            if (hideHud && ui && ui->IsShowingMenus()) {
                ui->ShowMenus(false);
                s_hidHud = true;
            }
            if (hideHair) HideHair(player);
            const int effectsHidden = hideEffects ? HideEffects(player) : 0;
            if (auto* face = player->GetFaceGenAnimationData(); face && !s_expressionSet) {
                face->ClearExpressionOverride();
                face->Reset(0.0f, true, true, true, false);
            }
            s_active = true;

            json result = {{"freeCamera", true}, {"hudHidden", s_hidHud}, {"hiddenNodes", s_hiddenNodes}, {"effectsHidden", effectsHidden}};
            if (auto* face = player->GetFaceGenAnimationData()) {
                result["eyes"] = {{"blinkStage", static_cast<int>(face->eyesBlinkingStage)},
                                  {"headingOffset", face->eyesHeadingOffset},
                                  {"pitchOffset", face->eyesPitchOffset}};
            }
            if (auto* prefs = RE::INIPrefSettingCollection::GetSingleton()) {
                auto* w = prefs->GetSetting("iSize W:Display");
                auto* h = prefs->GetSetting("iSize H:Display");
                if (w && h) result["screen"] = {{"width", w->data.i}, {"height", h->data.i}};
            }
            return result;
        }

        json Aim(float angleDeg, float distance, float heightOffset, bool aimFromHead, const std::string& refId) {
            auto* camera = RE::PlayerCamera::GetSingleton();
            RE::Actor* player = refId.empty() ? RE::PlayerCharacter::GetSingleton() : Helpers::ResolveActor(refId);
            if (!camera || !player) return {{"error", refId.empty() ? "Camera or player not available" : "Actor not found: " + refId}};
            if (!camera->IsInFreeCameraMode() || !camera->currentState) return {{"error", "Not in free camera: call enter first"}};
            auto* state = static_cast<RE::FreeCameraState*>(camera->currentState.get());
            auto* head = HeadNode(player);
            if (!head) return {{"error", refId.empty() ? "The player's head isn't loaded" : "That actor's head isn't loaded (too far away?)"}};

            // Skyrim headings: 0 = +Y, clockwise seen from above, so the facing direction is (sin, cos).
            constexpr float pi = std::numbers::pi_v<float>;
            const float bodyFacing = player->GetAngleZ();
            float facing = bodyFacing;
            bool fromHead = false;
            if (aimFromHead) fromHead = HeadHeading(head, bodyFacing, facing);
            if (!fromHead) facing = bodyFacing;
            const float around = facing + angleDeg * pi / 180.0f;
            // BUG-041: the head node sits behind the face (about the middle of the skull), which is fine head-on but
            // leaves a side view framed on the back of the head. Aim at a point toward the face, more so the further
            // round the camera goes (none head-on, so front captures are unchanged).
            const float toFace = 8.0f * std::abs(std::sin(angleDeg * pi / 180.0f));
            const RE::NiPoint3 target = head->world.translate + RE::NiPoint3{ std::sin(facing) * toFace, std::cos(facing) * toFace, heightOffset };
            const RE::NiPoint3 position = target + RE::NiPoint3{ std::sin(around), std::cos(around), 0.0f } * distance;
            RE::NiPoint3 look = target - position;
            look.Unitize();

            float yaw = around + pi;
            while (yaw >= 2 * pi) yaw -= 2 * pi;
            while (yaw < 0) yaw += 2 * pi;

            state->translation = position;
            s_aimedAt = position;
            s_aimed = true;
            // Which rotation component is yaw isn't documented: try one, check where the camera looks,
            // and swap if it's wrong.
            state->rotation.x = 0.0f;
            state->rotation.y = yaw;
            float alignment = Alignment(state, look);
            const char* layout = "rotation.y = yaw";
            if (alignment < 0.95f) {
                state->rotation.x = yaw;
                state->rotation.y = 0.0f;
                const float swapped = Alignment(state, look);
                if (swapped > alignment) {
                    alignment = swapped;
                    layout = "rotation.x = yaw";
                } else {
                    state->rotation.x = 0.0f;
                    state->rotation.y = yaw;
                }
            }

            return {{"camera", {position.x, position.y, position.z}},
                    {"head", {target.x, target.y, target.z}},
                    {"facingDeg", facing * 180.0f / pi},
                    {"bodyFacingDeg", bodyFacing * 180.0f / pi},
                    {"aimedFrom", fromHead ? "head" : "body"},
                    {"towardFace", toFace},
                    {"angleDeg", angleDeg},
                    {"distance", distance},
                    {"rotationLayout", layout},
                    {"alignment", alignment},
                    {"aimed", alignment > 0.95f}};
        }

        json SculptSnapshot() {
            // FEAT-233: where every vertex of the player's head parts lands on the screen right now (call while the
            // capture camera is frozen in place), so the server can pick the vertices under a face landmark and
            // turn a move on the picture into a sculpt offset in the mesh's own space.
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* npc = player ? player->GetActorBase() : nullptr;
            auto* root = player ? player->Get3D(false) : nullptr;
            auto* camera = RE::Main::WorldRootCamera();
            if (!npc || !root || !camera) return {{"error", "Player 3D or camera not available"}};
            auto* head = HeadNode(player);
            if (!head) return {{"error", "The player's head isn't loaded"}};
            const RE::NiPoint3 camPos = camera->world.translate;

            // Screen y from WorldPtToScreenPt3 may grow upward: check with a point above the head and report top-down.
            float hx, hy, hz, ux, uy, uz;
            if (!camera->WorldPtToScreenPt3(head->world.translate, hx, hy, hz, 1e-5f) ||
                !camera->WorldPtToScreenPt3(head->world.translate + RE::NiPoint3{ 0, 0, 10 }, ux, uy, uz, 1e-5f)) {
                return {{"error", "The head isn't on screen: take the snapshot during a face capture"}};
            }
            const bool yUp = uy > hy;
            auto project = [&](const RE::NiPoint3& w, float& x, float& y, float& dist) {
                float z;
                if (!camera->WorldPtToScreenPt3(w, x, y, z, 1e-5f)) return false;
                if (yUp) y = 1.0f - y;
                dist = (w - camPos).Length();
                return true;
            };

            std::vector<RE::BGSHeadPart*> parts;
            for (int i = 0; npc->headParts && i < npc->numHeadParts; i++) {
                if (auto* part = npc->headParts[i]) {
                    parts.push_back(part);
                    for (auto* extra : part->extraParts) if (extra) parts.push_back(extra);
                }
            }

            json hosts = json::array();
            for (auto* part : parts) {
                const char* host = part->morphs[RE::BGSHeadPart::MorphIndex::kChargenMorph].GetModel();
                const char* name = part->GetFormEditorID();
                if (!host || !host[0] || !name || !name[0]) continue;
                auto* node = root->GetObjectByName(RE::BSFixedString(name));
                if (!node) continue;
                RE::BSDynamicTriShape* shape = nullptr;
                RE::BSVisit::TraverseScenegraphGeometries(node, [&](RE::BSGeometry* geo) {
                    if (auto* dyn = netimmerse_cast<RE::BSDynamicTriShape*>(geo)) { shape = dyn; return RE::BSVisit::BSVisitControl::kStop; }
                    return RE::BSVisit::BSVisitControl::kContinue;
                });
                if (!shape) continue;

                // Mesh (skin) space to world: through the head bone when skinned to it.
                RE::NiTransform toWorld = shape->world;
                auto& geoData = shape->GetGeometryRuntimeData();
                if (auto* skin = geoData.skinInstance.get(); skin && skin->skinData && skin->bones) {
                    const auto count = skin->skinData->bones;
                    std::uint32_t pick = 0;
                    for (std::uint32_t b = 0; b < count; b++) {
                        if (skin->bones[b] && std::string_view(skin->bones[b]->name.c_str()) == "NPC Head [Head]") { pick = b; break; }
                    }
                    if (count > 0 && skin->bones[pick]) toWorld = skin->bones[pick]->world * skin->skinData->boneData[pick].skinToBone;
                }

                auto& dyn = shape->GetDynamicTrishapeRuntimeData();
                const std::uint32_t vertexCount = shape->GetTrishapeRuntimeData().vertexCount;
                json xy = json::array();
                RE::NiPoint3 centre{};
                std::uint32_t used = 0;
                {
                    RE::BSSpinLockGuard guard(dyn.lock);
                    const auto* data = static_cast<const float*>(dyn.dynamicData);
                    const std::uint32_t n = data ? std::min<std::uint32_t>(vertexCount, dyn.dataSize / 16) : 0;
                    for (std::uint32_t v = 0; v < n; v++) {
                        const RE::NiPoint3 local{ data[v * 4], data[v * 4 + 1], data[v * 4 + 2] };
                        float x = -1, y = -1, dist = -1;
                        project(toWorld * local, x, y, dist);
                        xy.push_back(std::round(x * 100000.0f) / 100000.0f);
                        xy.push_back(std::round(y * 100000.0f) / 100000.0f);
                        xy.push_back(std::round(dist * 1000.0f) / 1000.0f);
                        centre += local;
                        ++used;
                    }
                }
                if (used == 0) continue;
                centre = centre / static_cast<float>(used);

                // How a step of one mesh unit along each mesh axis moves on screen (x, y fractions) and in distance
                // from the camera, at the part's centre: the server inverts this to turn a picture move into a
                // mesh offset.
                json jacobian = json::array();
                float cx, cy, cd;
                project(toWorld * centre, cx, cy, cd);
                const RE::NiPoint3 axes[3] = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
                for (const auto& axis : axes) {
                    float x, y, d;
                    project(toWorld * (centre + axis), x, y, d);
                    jacobian.push_back({ x - cx, y - cy, d - cd });
                }
                hosts.push_back({{"host", host},
                                 {"part", name},
                                 {"type", static_cast<int>(std::to_underlying(part->type.get()))},
                                 {"vertices", vertexCount},
                                 {"xyd", xy},
                                 {"jacobian", jacobian}});
            }
            return {{"hosts", hosts}, {"yFlipped", yUp}};
        }

        json Check() {
            // BUG-056: once a capture came back as the ordinary third-person view: confirm the free camera is still on and
            // where Aim put it, right before the screenshot.
            auto* camera = RE::PlayerCamera::GetSingleton();
            const bool free = camera && camera->IsInFreeCameraMode() && camera->currentState;
            float offset = -1.0f;
            if (free && s_aimed) {
                auto* state = static_cast<RE::FreeCameraState*>(camera->currentState.get());
                offset = (state->translation - s_aimedAt).Length();
            }
            return {{"freeCamera", free}, {"offsetFromAim", offset}, {"ok", free && s_aimed && offset >= 0.0f && offset < 2.0f}};
        }

        json Exit() {
            auto* camera = RE::PlayerCamera::GetSingleton();
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* main = RE::Main::GetSingleton();
            json result = {{"wasActive", s_active}};

            int shown = 0;
            if (auto* root = player ? player->Get3D(false) : nullptr) {
                for (const auto& name : s_hiddenNodes) {
                    if (auto* node = root->GetObjectByName(RE::BSFixedString(name))) {
                        node->SetAppCulled(false);
                        ++shown;
                    }
                }
            }
            result["nodesShown"] = shown;
            s_hiddenNodes.clear();
            for (auto& effect : s_hiddenEffects) {
                if (effect) effect->SetAppCulled(false);
            }
            result["effectsShown"] = s_hiddenEffects.size();
            s_hiddenEffects.clear();

            if (s_hidHud) {
                if (auto* ui = RE::UI::GetSingleton()) ui->ShowMenus(true);
                s_hidHud = false;
            }
            if (camera && camera->IsInFreeCameraMode() && (!s_active || !s_wasFreeCamera)) camera->ToggleFreeCameraMode(false);
            if (main && s_active && !s_wasFrozen) main->GetRuntimeData().freezeTime = false;
            if (s_expressionSet && player) {
                if (auto* face = player->GetFaceGenAnimationData()) {
                    face->ClearExpressionOverride();
                    face->Reset(0.0f, true, true, false, false);  // expression and phonemes (mouthOpen)
                }
                s_expressionSet = false;
                result["expressionCleared"] = true;
            }
            s_active = false;
            s_aimed = false;
            result["freeCamera"] = camera && camera->IsInFreeCameraMode();
            return result;
        }
    }
}

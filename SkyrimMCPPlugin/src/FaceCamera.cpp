#include "FaceCamera.h"
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>
#include <cmath>
#include <format>
#include <numbers>
#include <string>
#include <vector>

namespace SkyrimMCP {

    namespace {
        // What Enter changed, so Exit can put it back. Names, not pointers: the 3D can be rebuilt between
        // calls, and this DLL can be reloaded.
        bool s_active = false;
        bool s_wasFreeCamera = false;
        bool s_wasFrozen = false;
        bool s_hidHud = false;
        bool s_expressionSet = false;  // Prepare set an expression: Enter keeps it, Exit clears it
        std::vector<std::string> s_hiddenNodes;

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

    namespace FaceCamera {

        json Prepare(int expression, float strength) {
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!player) return {{"error", "Player not available"}};
            // Forcing the default idle with a weapon or spell out leaves the hands stuck readied (BUG-029):
            // sheathe first and let the caller wait for the animation, then call Prepare again.
            if (auto* state = player->AsActorState(); state && state->IsWeaponDrawn()) {
                if (state->GetWeaponState() == RE::WEAPON_STATE::kDrawn) player->DrawWeaponMagicHands(false);
                return {{"sheathing", true}};
            }
            const bool idleReset = player->NotifyAnimationGraph(RE::BSFixedString("IdleForceDefaultState"));
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
            }
            return {{"idleReset", idleReset}, {"expressionReset", expressionReset}, {"expression", s_expressionSet ? expression : -1}};
        }

        json Enter(bool hideHud, bool hideHair) {
            auto* camera = RE::PlayerCamera::GetSingleton();
            auto* player = RE::PlayerCharacter::GetSingleton();
            auto* main = RE::Main::GetSingleton();
            if (!camera || !player || !main) return {{"error", "Camera or player not available"}};
            if (!HeadNode(player)) return {{"error", "The player's head isn't loaded (third-person 3D missing)"}};

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
            if (auto* face = player->GetFaceGenAnimationData(); face && !s_expressionSet) {
                face->ClearExpressionOverride();
                face->Reset(0.0f, true, true, true, false);
            }
            s_active = true;

            json result = {{"freeCamera", true}, {"hudHidden", s_hidHud}, {"hiddenNodes", s_hiddenNodes}};
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

        json Aim(float angleDeg, float distance, float heightOffset, bool aimFromHead) {
            auto* camera = RE::PlayerCamera::GetSingleton();
            auto* player = RE::PlayerCharacter::GetSingleton();
            if (!camera || !player) return {{"error", "Camera or player not available"}};
            if (!camera->IsInFreeCameraMode() || !camera->currentState) return {{"error", "Not in free camera: call enter first"}};
            auto* state = static_cast<RE::FreeCameraState*>(camera->currentState.get());
            auto* head = HeadNode(player);
            if (!head) return {{"error", "The player's head isn't loaded"}};

            // Skyrim headings: 0 = +Y, clockwise seen from above, so the facing direction is (sin, cos).
            constexpr float pi = std::numbers::pi_v<float>;
            const float bodyFacing = player->GetAngleZ();
            float facing = bodyFacing;
            bool fromHead = false;
            if (aimFromHead) fromHead = HeadHeading(head, bodyFacing, facing);
            if (!fromHead) facing = bodyFacing;
            const float around = facing + angleDeg * pi / 180.0f;
            const RE::NiPoint3 target = head->world.translate + RE::NiPoint3{ 0.0f, 0.0f, heightOffset };
            const RE::NiPoint3 position = target + RE::NiPoint3{ std::sin(around), std::cos(around), 0.0f } * distance;
            RE::NiPoint3 look = target - position;
            look.Unitize();

            float yaw = around + pi;
            while (yaw >= 2 * pi) yaw -= 2 * pi;
            while (yaw < 0) yaw += 2 * pi;

            state->translation = position;
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
                    {"angleDeg", angleDeg},
                    {"distance", distance},
                    {"rotationLayout", layout},
                    {"alignment", alignment},
                    {"aimed", alignment > 0.95f}};
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

            if (s_hidHud) {
                if (auto* ui = RE::UI::GetSingleton()) ui->ShowMenus(true);
                s_hidHud = false;
            }
            if (camera && camera->IsInFreeCameraMode() && (!s_active || !s_wasFreeCamera)) camera->ToggleFreeCameraMode(false);
            if (main && s_active && !s_wasFrozen) main->GetRuntimeData().freezeTime = false;
            if (s_expressionSet && player) {
                if (auto* face = player->GetFaceGenAnimationData()) face->ClearExpressionOverride();
                s_expressionSet = false;
                result["expressionCleared"] = true;
            }
            s_active = false;
            result["freeCamera"] = camera && camera->IsInFreeCameraMode();
            return result;
        }
    }
}

#pragma once

#include <nlohmann/json.hpp>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Face capture camera (FEAT-229): puts the free camera in front of the player's head with time
    // frozen, so the AI can take the same framing every time. Enter, then Aim (the free-camera state
    // only exists from the next frame), take the screenshot, then Exit to put everything back.
    namespace FaceCamera {
        // Neutral pose before a capture: back to the default idle (IdleForceDefaultState) and a neutral
        // expression. Leave the animation a moment to settle before Enter freezes time.
        json Prepare();

        // Free camera with time frozen (like "tfc 1"); optionally hides the HUD and the hair.
        json Enter(bool hideHud, bool hideHair);

        // Places the camera angleDeg around the head (0 = in front, 90 = on the character's right,
        // -90 = on their left), distance units away, heightOffset above the head node, looking at the head.
        // aimFromHead: angles count from the way the head faces (its bone), not the body, so a turned head
        // is still shot straight on.
        json Aim(float angleDeg, float distance, float heightOffset, bool aimFromHead);

        // Restores hair, HUD and camera mode.
        json Exit();
    }
}

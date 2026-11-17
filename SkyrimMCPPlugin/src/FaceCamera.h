#pragma once

#include <nlohmann/json.hpp>

namespace SkyrimMCP {

    using json = nlohmann::json;

    // Face capture camera (FEAT-229): puts the free camera in front of the player's head with time
    // frozen, so the AI can take the same framing every time. Enter, then Aim (the free-camera state
    // only exists from the next frame), take the screenshot, then Exit to put everything back.
    namespace FaceCamera {
        // Neutral pose before a capture: back to the default idle (IdleForceDefaultState) and a neutral
        // expression. Leave the animation a moment to settle before Enter freezes time. With a weapon or
        // spell out it only starts sheathing and returns {"sheathing": true}: wait, then call it again.
        // While the player moves it does nothing and returns {"moving": true}.
        // expression: 0-16 (the game's expression list, e.g. 2 = DialogueHappy) held at strength 0-1 through
        // the capture and cleared by Exit; -1 = neutral.
        // mouthOpen 0-1 opens the jaw (phoneme BigAah), for an open-mouthed smile.
        json Prepare(int expression, float strength, float mouthOpen);

        // Free camera with time frozen (like "tfc 1"); optionally hides the HUD and the hair. Mid-blink it
        // returns {"blinking": true} without entering: wait a moment and call it again.
        // hideEffects culls particle effects on the character (cold-weather breath) for the shot.
        json Enter(bool hideHud, bool hideHair, bool hideEffects);

        // Places the camera angleDeg around the head (0 = in front, 90 = on the character's right,
        // -90 = on their left), distance units away, heightOffset above the head node, looking at the head.
        // aimFromHead: angles count from the way the head faces (its bone), not the body, so a turned head
        // is still shot straight on.
        json Aim(float angleDeg, float distance, float heightOffset, bool aimFromHead);

        // FEAT-233: every vertex of the player's head parts projected onto the screen (x, y top-down in 0-1, and
        // distance from the camera), per part, with the part's chargen TRI path (RaceMenu's sculpt host) and how
        // one mesh unit along each axis moves on screen. Call between Aim and Exit.
        json SculptSnapshot();

        // Restores hair, HUD and camera mode.
        json Exit();
    }
}

#pragma once

namespace SkyrimMCP::HostFaceRefresh {
    // FEAT-249: after a save loads or the player's cell changes, asks the logic DLL (reapply_face_state) to put back what
    // SkyLink keeps per character that the game and RaceMenu don't (the brow colour). Runs twice, a few seconds after
    // the event and again later, so it lands after RaceMenu's own reload of tints and hair. Repeated triggers coalesce.
    void Schedule(const char* a_reason);
}

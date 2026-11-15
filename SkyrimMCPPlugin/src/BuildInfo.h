#pragma once

#include "Version.h"

#include <string>

namespace SkyrimMCP::BuildInfo {
    // When this DLL was linked (UTC), read from its own PE header. Unlike __DATE__,
    // it changes on every build, even when no file that uses it was recompiled.
    const std::string& GetBuildTime();
}

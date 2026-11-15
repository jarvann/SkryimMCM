#pragma once

#include "HostApi.h"

namespace SkyrimMCP::LogicHost {

    // The services the host DLL gave this logic DLL at SkyLink_Init (see HostApi.h).
    // Null before init, after shutdown, and never null in between.
    const SkyLinkHostApi* Get();

}

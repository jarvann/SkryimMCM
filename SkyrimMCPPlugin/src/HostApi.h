#pragma once

// The interface between the two halves of the SkyLink plugin:
//
//   SkyrimMCPPlugin.dll        the host. SKSE loads it at startup and it stays loaded:
//                              pipe server, game event sinks, game-thread task queue,
//                              and the loader for the logic DLL.
//   SkyLinkAI\SkyLinkLogic.dll the logic: every command handler. The host loads a temp
//                              copy of it and can swap in a newer build while the game
//                              runs (hot reload).
//
// The two DLLs each link their own C runtime and their own copy of CommonLib, so only
// plain C types cross this boundary: no C++ objects, no memory freed on the other side,
// no exceptions. Strings come back through a write callback that copies them.
//
// Bump SKYLINK_ABI_VERSION whenever this file changes; the host refuses a logic DLL
// built against a different version instead of calling into it.

#include <cstddef>
#include <cstdint>

#define SKYLINK_ABI_VERSION 1

extern "C" {

    // Copies `len` bytes of `data` into whatever `ctx` points at (owned by the caller).
    using SkyLinkWriteFn = void (*)(void* ctx, const char* data, std::size_t len);

    // A unit of work for the game thread. Runs once; `ctx` is owned by the logic DLL.
    using SkyLinkTaskFn = void (*)(void* ctx);

    struct SkyLinkHostApi {
        std::uint32_t abiVersion;

        // const SKSE::LoadInterface*; null when the logic DLL is loaded outside Skyrim
        // (the reload test harness).
        const void* skseLoadInterface;

        // Writes one line to the plugin log. `level` uses spdlog's level values.
        void (*log)(int level, const char* message, std::size_t len);

        // Runs fn(ctx) on the game thread. Returns 0 when queued. The host owns the
        // SKSE task object, so a queued task never refers to code in the logic DLL
        // except `fn` itself, and the host counts tasks not yet run: it will not unload
        // the logic DLL while any remain.
        int (*addTask)(SkyLinkTaskFn fn, void* ctx);

        // Papyrus VM calls. The callback object the VM holds on to is host code, so a
        // VM call that is still running (or that timed out on our side) never leaves
        // the VM pointing into an unloaded logic DLL.
        //   papyrusCallbackCreate: a new RE::BSScript::IStackCallbackFunctor* with a
        //     reference count of 0; wrap it in a BSTSmartPointer straight away. Null on
        //     failure.
        //   papyrusCallbackWait: waits for the VM to call it back. Returns 0 and writes
        //     {"result": <value>} (or {"error": "..."}) as JSON; returns 1 on timeout.
        //     Only call it while holding a reference to the callback.
        void* (*papyrusCallbackCreate)();
        int (*papyrusCallbackWait)(void* callback, std::uint32_t timeoutMs, SkyLinkWriteFn write, void* ctx);
    };

    // Exports of SkyLinkLogic.dll, looked up by name with GetProcAddress.
    using SkyLinkAbiVersionFn = std::uint32_t (*)();                         // "SkyLink_AbiVersion"
    using SkyLinkInitFn = int (*)(const SkyLinkHostApi* host);               // "SkyLink_Init": 0 = ok
    using SkyLinkHandleRequestFn = void (*)(const char* request, std::size_t len,
        SkyLinkWriteFn write, void* ctx);                                    // "SkyLink_HandleRequest": one JSON line in, one out
    using SkyLinkGetInfoFn = void (*)(SkyLinkWriteFn write, void* ctx);      // "SkyLink_GetInfo": JSON {"version","built"}
    // "SkyLink_Shutdown": called just before the host unloads the DLL, once no request
    // is running and no game-thread task of its is queued. Releases what the DLL holds;
    // the DLL makes no further calls into the host after it returns.
    using SkyLinkShutdownFn = void (*)();
}

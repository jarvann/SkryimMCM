cmake_minimum_required(VERSION 3.21)

project(
    SkyrimMCPPlugin
    VERSION 1.2.1
    LANGUAGES CXX
)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Find dependencies
find_package(nlohmann_json CONFIG REQUIRED)

# Disable CommonLibSSE tests and VR
set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_VR OFF CACHE BOOL "" FORCE)

# Patch-safety checks only guard Trampoline::write_branch, which this plugin never
# uses. Leaving them on makes CommonLib git-clone MinHook at configure time.
set(SKSE_SUPPORT_PATCH_SAFETY OFF CACHE BOOL "" FORCE)

# CommonLibSSE-NG from local clone.
# Must be the alandtse fork at v16.3.0 or newer: Skyrim 1.7.99+ uses Address
# Library format 5, and SKSE 2.3.x refuses any plugin that does not declare it.
#   git clone --depth 1 --branch v16.3.0 https://github.com/alandtse/CommonLibVR.git extern/CommonLibSSE-NG
# Always built from source: the published prebuilt bundles use the /MD runtime
# and all three runtimes, which does not match our static /MT, SE+AE build.
add_subdirectory(extern/CommonLibSSE-NG)

# The plugin is two DLLs (see src/HostApi.h):
#   SkyrimMCPPlugin.dll         host: loaded by SKSE, stays loaded for the session
#   SkyLinkAI/SkyLinkLogic.dll  logic: every command handler; the host can reload it
#                               while the game runs, so most changes need no restart
set(HOST_SOURCES
    src/BuildInfo.cpp
    src/CrashRecorder.cpp
    src/EventSystem.cpp
    src/HostCore.cpp
    src/LogicLoader.cpp
    src/Main.cpp
    src/PipeServer.cpp
    src/SkseInterfaceProxy.cpp
)

set(LOGIC_SOURCES
    src/BuildInfo.cpp
    src/CombatAnalysis.cpp
    src/InventoryManager.cpp
    src/LogicMain.cpp
    src/NPCManager.cpp
    src/PapyrusBridge.cpp
    src/PlayerQueries.cpp
    src/AppearanceManager.cpp
    src/BlueprintManager.cpp
    src/LootManager.cpp
    src/ScriptStats.cpp
    src/Protocol.cpp
    src/QuestManager.cpp
    src/TaskQueue.cpp
    src/UIManager.cpp
    src/UtilityManager.cpp
    src/WorldManager.cpp
)

# Version header, generated from project(VERSION): change the version there.
configure_file(src/Version.h.in "${CMAKE_BINARY_DIR}/generated/Version.h" @ONLY)

# One Windows version resource per DLL (Properties > Details in Explorer).
function(skylink_version_resource OUT_VAR NAME DESCRIPTION)
    set(SKYLINK_RC_NAME "${NAME}")
    set(SKYLINK_RC_DESCRIPTION "${DESCRIPTION}")
    set(rc "${CMAKE_BINARY_DIR}/generated/${NAME}.rc")
    configure_file(src/version.rc.in "${rc}" @ONLY)
    set(${OUT_VAR} "${rc}" PARENT_SCOPE)
endfunction()

function(skylink_dll TARGET)
    target_include_directories(${TARGET} PRIVATE src "${CMAKE_BINARY_DIR}/generated")
    target_link_libraries(${TARGET} PRIVATE
        CommonLibSSE::CommonLibSSE
        nlohmann_json::nlohmann_json
    )
    target_compile_definitions(${TARGET} PRIVATE
        NOMINMAX
        WIN32_LEAN_AND_MEAN
    )
    set_target_properties(${TARGET} PROPERTIES PREFIX "")
endfunction()

skylink_version_resource(HOST_RC SkyrimMCPPlugin "SkyLink AI SKSE plugin")
add_library(SkyrimMCPPlugin SHARED ${HOST_SOURCES} "${HOST_RC}")
skylink_dll(SkyrimMCPPlugin)
set_target_properties(SkyrimMCPPlugin PROPERTIES OUTPUT_NAME "SkyrimMCPPlugin")

skylink_version_resource(LOGIC_RC SkyLinkLogic "SkyLink AI command handlers (reloadable)")
add_library(SkyLinkLogic SHARED ${LOGIC_SOURCES} "${LOGIC_RC}")
skylink_dll(SkyLinkLogic)
set_target_properties(SkyLinkLogic PROPERTIES OUTPUT_NAME "SkyLinkLogic")

# Building the plugin builds both halves.
add_dependencies(SkyrimMCPPlugin SkyLinkLogic)

# Copy both DLLs to output/ in the layout they are installed in under SKSE\Plugins.
add_custom_command(
    TARGET SkyrimMCPPlugin
    POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:SkyrimMCPPlugin>"
        "${CMAKE_SOURCE_DIR}/output/$<TARGET_FILE_NAME:SkyrimMCPPlugin>"
    COMMENT "Copying plugin DLL to output/"
)
add_custom_command(
    TARGET SkyLinkLogic
    POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_SOURCE_DIR}/output/SkyLinkAI"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:SkyLinkLogic>"
        "${CMAKE_SOURCE_DIR}/output/SkyLinkAI/$<TARGET_FILE_NAME:SkyLinkLogic>"
    COMMENT "Copying logic DLL to output/SkyLinkAI/"
)

# Test harness for the reload machinery: loads and swaps SkyLinkLogic.dll outside
# Skyrim. Local testing only, never shipped. Configure with -DSKYLINK_BUILD_TESTS=ON.
option(SKYLINK_BUILD_TESTS "Build the logic-DLL reload test harness" OFF)
if(SKYLINK_BUILD_TESTS)
    add_executable(SkyLinkReloadHarness tests/ReloadHarness.cpp src/LogicLoader.cpp)
    target_include_directories(SkyLinkReloadHarness PRIVATE src)
    target_link_libraries(SkyLinkReloadHarness PRIVATE nlohmann_json::nlohmann_json)
    target_compile_definitions(SkyLinkReloadHarness PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    add_dependencies(SkyLinkReloadHarness SkyLinkLogic)
endif()

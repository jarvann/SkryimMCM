cmake_minimum_required(VERSION 3.21)

project(
    SkyrimMCPPlugin
    VERSION 1.1.0
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

# Plugin sources
set(SOURCES
    src/CombatAnalysis.cpp
    src/EventSystem.cpp
    src/InventoryManager.cpp
    src/Main.cpp
    src/NPCManager.cpp
    src/PapyrusBridge.cpp
    src/PipeServer.cpp
    src/PlayerQueries.cpp
    src/Protocol.cpp
    src/QuestManager.cpp
    src/TaskQueue.cpp
    src/UIManager.cpp
    src/UtilityManager.cpp
    src/WorldManager.cpp
)

set(HEADERS
    src/CombatAnalysis.h
    src/EventSystem.h
    src/GameInterface.h
    src/Helpers.h
    src/InventoryManager.h
    src/NPCManager.h
    src/PapyrusBridge.h
    src/PipeServer.h
    src/PlayerQueries.h
    src/Protocol.h
    src/QuestManager.h
    src/TaskQueue.h
    src/UIManager.h
    src/UtilityManager.h
    src/WorldManager.h
)

# Create the SKSE plugin as a shared library (DLL)
add_library(${PROJECT_NAME} SHARED ${SOURCES} ${HEADERS})

target_include_directories(${PROJECT_NAME} PRIVATE src)

target_link_libraries(${PROJECT_NAME} PRIVATE
    CommonLibSSE::CommonLibSSE
    nlohmann_json::nlohmann_json
)

target_compile_definitions(${PROJECT_NAME} PRIVATE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
)

# Set output name without "lib" prefix
set_target_properties(${PROJECT_NAME} PROPERTIES
    PREFIX ""
    OUTPUT_NAME "SkyrimMCPPlugin"
)

# Copy output to a convenient location
add_custom_command(
    TARGET ${PROJECT_NAME}
    POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:${PROJECT_NAME}>"
        "${CMAKE_SOURCE_DIR}/output/$<TARGET_FILE_NAME:${PROJECT_NAME}>"
    COMMENT "Copying plugin DLL to output/"
)

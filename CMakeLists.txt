cmake_minimum_required(VERSION 3.20)
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/VERSION" CB_VERSION)
string(STRIP "${CB_VERSION}" CB_VERSION)
project(ContextBridge VERSION ${CB_VERSION} LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static CRT everywhere: the handler must not depend on a VC++ redistributable, and all
# targets must agree on the runtime to link together.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(MSVC)
    # Sources contain UTF-8 (Japanese) literals.
    add_compile_options(/utf-8 /EHsc)
endif()

# Platform-independent core: config model, matching, templates. Unit-tested everywhere.
add_library(cb_core STATIC
    src/Shell/core/json.cpp
    src/Shell/core/strings.cpp
    src/Shell/core/config.cpp)
target_include_directories(cb_core PUBLIC src/Shell/core)

option(CB_BUILD_TESTS "Build native unit tests" ON)
if(CB_BUILD_TESTS)
    enable_testing()
    add_executable(cb_core_tests tests/native/core_tests.cpp)
    target_link_libraries(cb_core_tests PRIVATE cb_core)
    add_test(NAME cb_core_tests
             COMMAND cb_core_tests ${CMAKE_CURRENT_SOURCE_DIR}/tests/vectors/vectors.json)
endif()

if(WIN32)
    enable_language(RC)
    configure_file(src/Shell/version.h.in ${CMAKE_CURRENT_BINARY_DIR}/generated/version.h)

    add_library(ContextBridge.Shell SHARED
        src/Shell/win/dllmain.cpp
        src/Shell/win/commands.cpp
        src/Shell/win/config_store.cpp
        src/Shell/win/launcher.cpp
        src/Shell/win/log.cpp
        src/Shell/shell.rc
        src/Shell/ContextBridge.Shell.def)
    target_include_directories(ContextBridge.Shell PRIVATE ${CMAKE_CURRENT_BINARY_DIR}/generated src/Shell)
    target_compile_definitions(ContextBridge.Shell PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
                               _WIN32_WINNT=0x0A00 NTDDI_VERSION=0x0A00000B)
    target_link_libraries(ContextBridge.Shell PRIVATE cb_core shlwapi shell32 ole32 advapi32 user32)
    set_target_properties(ContextBridge.Shell PROPERTIES PREFIX "" OUTPUT_NAME "ContextBridge.Shell")

    if(MSVC)
        target_compile_options(ContextBridge.Shell PRIVATE /W4 /guard:cf)
        target_link_options(ContextBridge.Shell PRIVATE /guard:cf /DYNAMICBASE /NXCOMPAT)
    else()
        target_compile_options(ContextBridge.Shell PRIVATE -Wall -Wextra)
        target_link_options(ContextBridge.Shell PRIVATE -static -Wl,--dynamicbase -Wl,--nxcompat)
    endif()

    if(CB_BUILD_TESTS)
        # COM-level smoke test against the real DLL (needs Windows; run by the windows CI job).
        add_executable(cb_shell_smoke tests/native/shell_smoke.cpp)
        target_compile_definitions(cb_shell_smoke PRIVATE UNICODE _UNICODE)
        target_link_libraries(cb_shell_smoke PRIVATE ole32 shell32 uuid)
        if(NOT MSVC)
            target_link_options(cb_shell_smoke PRIVATE -municode -static)
        endif()
        add_dependencies(cb_shell_smoke ContextBridge.Shell)
    endif()
endif()

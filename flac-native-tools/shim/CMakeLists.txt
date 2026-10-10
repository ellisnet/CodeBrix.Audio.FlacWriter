# shim/CMakeLists.txt - builds the ONE native library `codebrix_flac` for CodeBrix.Audio.FlacWriter.
#
# libFLAC (flac-1.5.0) is built from the UNMODIFIED Xiph tree in ../flac through flac's OWN CMake
# (add_subdirectory, EXCLUDE_FROM_ALL), as a STATIC library, and linked into one shared library
# together with codebrix_flac.c. Only the functions declared in codebrix_flac.h are exported.
#
# flac's CMake is driven with:
#   BUILD_SHARED_LIBS=OFF       static libFLAC, folded into the shared codebrix_flac
#   BUILD_CXXLIBS=OFF           no libFLAC++
#   BUILD_PROGRAMS=OFF          no flac / metaflac (GPL programs - never built, never shipped)
#   BUILD_EXAMPLES=OFF  BUILD_TESTING=OFF  BUILD_DOCS=OFF  BUILD_UTILS=OFF
#   INSTALL_MANPAGES=OFF  INSTALL_PKGCONFIG_MODULES=OFF  INSTALL_CMAKE_CONFIG_MODULE=OFF
#   WITH_OGG=OFF                native FLAC only (no Ogg FLAC, no libogg)
#   ENABLE_MULTITHREADING=OFF   v1 = libFLAC's most-exercised single-threaded configuration
# Everything else is libFLAC's default (WITH_ASM / x86 intrinsics with runtime CPU detection, NEON
# on arm64, 64-bit words, _FORTIFY_SOURCE=2 + -fstack-protector-strong where the compiler has them).
#
# flac's src/CMakeLists.txt always DEFINES the src/share helper targets (grabbag, getopt, utf8,
# replaygain_*); EXCLUDE_FROM_ALL keeps them - and everything else not needed by codebrix_flac -
# out of the build. Only src/libFLAC (+ the BSD-3 headers it includes from include/share, and on
# Windows src/share/win_utf8_io/win_utf8_io.c, which libFLAC's own target lists) is compiled.
#
# Options:
#   CBF_BUILD_SMOKE_TEST=ON   also build ../smoke-test.c as `codebrix_flac_smoke`. The smoke test
#                             loads codebrix_flac BY PATH at run time (dlopen / LoadLibrary) and
#                             links its own static libFLAC only for its DECODER (verification).
cmake_minimum_required(VERSION 3.16)

project(codebrix_flac LANGUAGES C)

option(CBF_BUILD_SMOKE_TEST "Build the smoke-test executable" OFF)

set(CBF_TOOLS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/..")
if(NOT DEFINED CBF_FLAC_DIR)
    set(CBF_FLAC_DIR "${CBF_TOOLS_DIR}/flac")
endif()

if(NOT EXISTS "${CBF_FLAC_DIR}/src/libFLAC/stream_encoder.c" OR NOT EXISTS "${CBF_FLAC_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "Upstream flac tree not found at ${CBF_FLAC_DIR}.")
endif()

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

# Every object - libFLAC's included - is compiled with hidden visibility; codebrix_flac.h marks its
# own functions CBF_EXPORT (visibility("default") / __declspec(dllexport)). With a static libFLAC,
# FLAC__NO_DLL is defined and FLAC_API expands to nothing, so no libFLAC symbol is exported.
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# ---- libFLAC via flac's own CMake --------------------------------------------------------------
foreach(opt BUILD_SHARED_LIBS BUILD_CXXLIBS BUILD_PROGRAMS BUILD_EXAMPLES BUILD_TESTING BUILD_DOCS
            BUILD_UTILS INSTALL_MANPAGES INSTALL_PKGCONFIG_MODULES INSTALL_CMAKE_CONFIG_MODULE
            WITH_OGG ENABLE_MULTITHREADING)
    set(${opt} OFF CACHE BOOL "codebrix_flac: forced OFF" FORCE)
endforeach()
add_subdirectory("${CBF_FLAC_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/flac" EXCLUDE_FROM_ALL)

if(NOT TARGET FLAC)
    message(FATAL_ERROR "flac's CMake did not define the FLAC target.")
endif()

# ---- the shared library -------------------------------------------------------------------------
add_library(codebrix_flac SHARED codebrix_flac.c)
target_include_directories(codebrix_flac PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(codebrix_flac PRIVATE FLAC)
if(MSVC)
    target_compile_definitions(codebrix_flac PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(codebrix_flac PRIVATE /W4)
else()
    target_compile_options(codebrix_flac PRIVATE -Wall -Wextra)
endif()
# No SONAME version suffix: the managed loader probes the plain file name on every RID.
if(WIN32)
    set_target_properties(codebrix_flac PROPERTIES PREFIX "")
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "Linux" OR CMAKE_SYSTEM_NAME STREQUAL "Android")
    target_link_options(codebrix_flac PRIVATE
        "LINKER:--no-undefined"
        "LINKER:--exclude-libs,ALL"
        "LINKER:-z,noexecstack"
        "LINKER:--build-id=sha1")
endif()
if(APPLE)
    set_target_properties(codebrix_flac PROPERTIES INSTALL_NAME_DIR "@rpath")
endif()

# ---- smoke test ---------------------------------------------------------------------------------
if(CBF_BUILD_SMOKE_TEST)
    add_executable(codebrix_flac_smoke "${CBF_TOOLS_DIR}/smoke-test.c")
    # libFLAC here is ONLY the verifying decoder; the library under test is loaded by path.
    target_link_libraries(codebrix_flac_smoke PRIVATE FLAC ${CMAKE_DL_LIBS})
    if(MSVC)
        target_compile_definitions(codebrix_flac_smoke PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()
endif()

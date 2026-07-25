# Patch step for the pinned CTranslate2 checkout. Runs with the working directory set to the
# fetched source tree.
#
# Why: the spdlog submodule carried by this CTranslate2 pin vendors fmt 8.x, whose checked-iterator
# path is compiled whenever _SECURE_SCL is on -- that is, in every MSVC Debug build. It resolves to
# stdext::checked_array_iterator, which has been removed from the MSVC STL (absent in 14.51), so the
# whole Debug build of ctranslate2 dies inside format.h with C2653/C2061/C3613/C3646/C2988/C2059.
# Upstream fmt deleted the same branch for the same reason. The #else branch (checked_ptr = T*) is
# what every Release build already compiles, so disabling the branch makes Debug agree with Release
# rather than introducing behaviour that was never shipped.
#
# To remove this patch: drop PATCH_COMMAND from the ctranslate2 FetchContent_Declare in
# src/InfiniTranseon.ModelRuntime.Native/CMakeLists.txt and delete this file. Debug builds with
# INFINI_ENABLE_LOCAL_MODEL_RUNTIME=ON will then fail again on MSVC >= 14.44.

set(header "third_party/spdlog/include/spdlog/fmt/bundled/format.h")
set(guard "#if defined(_SECURE_SCL) && _SECURE_SCL")
set(replacement "#if 0 // Patched: stdext::checked_array_iterator no longer exists in the MSVC STL.")

if(NOT EXISTS "${header}")
    message(FATAL_ERROR
        "Vendored fmt header '${header}' is missing. The CTranslate2 pin changed; re-verify this patch.")
endif()

file(READ "${header}" contents)

string(FIND "${contents}" "${replacement}" already_patched)
if(NOT already_patched EQUAL -1)
    return()
endif()

string(FIND "${contents}" "${guard}" guard_offset)
if(guard_offset EQUAL -1)
    message(FATAL_ERROR
        "Guard '${guard}' was not found in '${header}'. The CTranslate2 pin changed; re-verify this patch.")
endif()

string(REPLACE "${guard}" "${replacement}" contents "${contents}")
file(WRITE "${header}" "${contents}")
message(STATUS "Patched vendored fmt for the MSVC STL: ${header}")

add_library(InfiniTranseon.Engine.Native STATIC
    src/abi/infini_engine.cpp
)

target_include_directories(InfiniTranseon.Engine.Native PUBLIC
    "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
)

target_compile_features(InfiniTranseon.Engine.Native PUBLIC cxx_std_20)

if(MSVC)
    target_compile_options(InfiniTranseon.Engine.Native PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

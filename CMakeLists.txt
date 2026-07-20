cmake_minimum_required(VERSION 3.28)

project(InfiniTranseon LANGUAGES CXX)

include(CTest)

add_subdirectory(src/InfiniTranseon.Engine.Native)
add_subdirectory(src/InfiniTranseon.EngineHost)
add_subdirectory(spikes/InfiniTranseon.CaptureSpike)

if(BUILD_TESTING)
    add_subdirectory(tests/InfiniTranseon.Engine.Tests)
endif()

add_executable(InfiniTranseon.EngineHost
    main.cpp
)

target_compile_features(InfiniTranseon.EngineHost PRIVATE cxx_std_20)
target_link_libraries(InfiniTranseon.EngineHost PRIVATE InfiniTranseon.Engine.Native)

if(MSVC)
    target_compile_options(InfiniTranseon.EngineHost PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

if(BUILD_TESTING)
    add_test(
        NAME infini_engine_host_abi_version
        COMMAND InfiniTranseon.EngineHost --abi-version
    )
    set_tests_properties(infini_engine_host_abi_version PROPERTIES
        PASS_REGULAR_EXPRESSION "^1"
    )
endif()

#include <cstdint>
#include <cstdlib>

#include <infini_engine.h>

namespace
{
void require(const bool condition)
{
    if (!condition)
    {
        std::abort();
    }
}
}

int main()
{
    static_assert(IT_ENGINE_ABI_VERSION == 1U);
    static_assert(sizeof(IT_EngineHandle*) == sizeof(void*));

    IT_RuntimeCapabilitiesV1 capabilities{};
    capabilities.struct_size = sizeof(capabilities);
    capabilities.abi_version = IT_ENGINE_ABI_VERSION;

    require(IT_EngineGetCapabilities(&capabilities) == IT_RESULT_OK);
    require(capabilities.max_capture_sources == 8U);
    require(capabilities.max_targets == 8U);
    require(capabilities.max_ipc_message_bytes == 8'388'608U);

    IT_EngineCreateOptionsV1 options{};
    options.struct_size = sizeof(options);
    options.abi_version = IT_ENGINE_ABI_VERSION;

    IT_EngineHandle* engine = nullptr;
    require(IT_EngineCreate(&options, &engine) == IT_RESULT_OK);
    require(engine != nullptr);
    require(IT_EngineDestroy(engine) == IT_RESULT_OK);

    options.struct_size = 0U;
    require(IT_EngineCreate(&options, &engine) == IT_RESULT_INVALID_STRUCTURE_SIZE);

    return EXIT_SUCCESS;
}

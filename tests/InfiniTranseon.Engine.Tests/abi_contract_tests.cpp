#include <cstdint>
#include <cstdlib>
#include <type_traits>

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
    static_assert(std::is_standard_layout_v<IT_RuntimeEnvelopeV1>);
    static_assert(sizeof(IT_Guid) == 16U);
    static_assert(IT_RUNTIME_MESSAGE_SHUTDOWN_ACKNOWLEDGEMENT == 18);

    IT_RuntimeEnvelopeV1 envelope{};
    envelope.struct_size = sizeof(envelope);
    envelope.abi_version = IT_ENGINE_ABI_VERSION;
    envelope.protocol_version = 1U;
    envelope.message_kind = IT_RUNTIME_MESSAGE_OCR_RESULT;
    require(envelope.payload.data == nullptr);
    require(envelope.payload.byte_count == 0U);

    IT_RuntimeCapabilitiesV1 capabilities{};
    capabilities.struct_size = sizeof(capabilities);
    capabilities.abi_version = IT_ENGINE_ABI_VERSION;

    require(IT_EngineGetCapabilities(&capabilities) == IT_RESULT_OK);
    require(capabilities.max_capture_sources == 8U);
    require(capabilities.max_targets == 8U);
    require(capabilities.max_ipc_message_bytes == 8'388'608U);

    return EXIT_SUCCESS;
}

#include "infini_model_runtime.h"

#include <array>
int main() {
    if (infini_model_runtime_abi_version() != 1) {
        return 1;
    }

    std::array<char, 128> error{};
    void* runtime = reinterpret_cast<void*>(1);
    const int32_t create_status = infini_model_runtime_create(
        nullptr,
        nullptr,
        &runtime,
        error.data(),
        error.size());
    if (create_status != INFINI_MODEL_INVALID_ARGUMENT ||
        runtime != nullptr ||
        error[0] == '\0') {
        return 2;
    }

    std::array<char, 64> output{};
    size_t output_bytes = 99;
    const int32_t translate_status = infini_model_runtime_translate(
        nullptr,
        "<2en>",
        "hello",
        100,
        output.data(),
        output.size(),
        &output_bytes,
        error.data(),
        error.size());
    if (translate_status != INFINI_MODEL_INVALID_ARGUMENT ||
        output_bytes != 0) {
        return 3;
    }

    infini_model_runtime_destroy(nullptr);
    return 0;
}

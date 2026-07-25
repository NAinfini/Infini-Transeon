#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define INFINI_MODEL_API __declspec(dllexport)
#define INFINI_MODEL_CALL __cdecl
#else
#define INFINI_MODEL_API
#define INFINI_MODEL_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum infini_model_status {
    INFINI_MODEL_OK = 0,
    INFINI_MODEL_INVALID_ARGUMENT = 1,
    INFINI_MODEL_LOAD_FAILED = 2,
    INFINI_MODEL_TRANSLATION_FAILED = 3,
    INFINI_MODEL_OUTPUT_TOO_LARGE = 4,
    INFINI_MODEL_OUT_OF_MEMORY = 5,
};

INFINI_MODEL_API uint32_t INFINI_MODEL_CALL infini_model_runtime_abi_version(void);

INFINI_MODEL_API int32_t INFINI_MODEL_CALL infini_model_runtime_create(
    const char* model_directory_utf8,
    const char* sentencepiece_model_utf8,
    void** runtime,
    char* error_utf8,
    size_t error_capacity);

INFINI_MODEL_API int32_t INFINI_MODEL_CALL infini_model_runtime_translate(
    void* runtime,
    const char* target_token_utf8,
    const char* source_text_utf8,
    size_t maximum_output_characters,
    char* output_utf8,
    size_t output_capacity,
    size_t* output_bytes,
    char* error_utf8,
    size_t error_capacity);

INFINI_MODEL_API void INFINI_MODEL_CALL infini_model_runtime_destroy(void* runtime);

#ifdef __cplusplus
}
#endif

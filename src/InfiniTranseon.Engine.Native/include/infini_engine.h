#ifndef INFINI_TRANSEON_ENGINE_H
#define INFINI_TRANSEON_ENGINE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define IT_CALL __cdecl
#else
#define IT_CALL
#endif

#if defined(__cplusplus)
#define IT_NOEXCEPT noexcept
extern "C" {
#else
#define IT_NOEXCEPT
#endif

#define IT_ENGINE_ABI_VERSION 1U

typedef struct IT_EngineHandle IT_EngineHandle;

typedef enum IT_Result
{
    IT_RESULT_OK = 0,
    IT_RESULT_INVALID_ARGUMENT = 1,
    IT_RESULT_INVALID_STRUCTURE_SIZE = 2,
    IT_RESULT_ABI_VERSION_MISMATCH = 3,
    IT_RESULT_OUT_OF_MEMORY = 4,
    IT_RESULT_INTERNAL_ERROR = 5
} IT_Result;

typedef void* (IT_CALL* IT_AllocateFn)(size_t byte_count, void* user_context);
typedef void (IT_CALL* IT_FreeFn)(void* allocation, void* user_context);

typedef struct IT_EngineCreateOptionsV1
{
    uint32_t struct_size;
    uint32_t abi_version;
    IT_AllocateFn allocate;
    IT_FreeFn free;
    void* allocator_context;
} IT_EngineCreateOptionsV1;

typedef struct IT_RuntimeCapabilitiesV1
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t max_capture_sources;
    uint32_t max_targets;
    uint32_t max_capture_dimension;
    uint64_t max_capture_pixels_per_source;
    uint32_t max_regions_per_target;
    uint32_t max_active_tracks_per_target;
    uint32_t max_ocr_boxes_per_result;
    uint32_t max_source_chars;
    uint32_t max_overlay_chars_per_target;
    uint32_t max_translation_channels_per_region;
    uint32_t max_outstanding_wgc_frames_per_source;
    uint32_t max_owned_frame_textures_per_source;
    uint32_t max_readback_crops_per_source;
    uint64_t max_readback_pixels_per_source_ring;
    uint64_t max_global_ocr_crop_bytes_in_flight;
    uint32_t max_mapped_readbacks_per_adapter;
    uint32_t max_mapped_readback_hold_milliseconds;
    uint64_t max_detection_pyramid_bytes_per_source;
    uint64_t max_overlay_surface_bytes_per_target;
    uint32_t max_ocr_sessions;
    uint64_t max_ocr_tensor_workspace_bytes;
    uint64_t max_engine_committed_bytes;
    uint64_t max_gpu_bytes_per_adapter_ceiling;
    uint32_t max_gpu_budget_percentage;
    uint32_t max_ipc_message_bytes;
    uint64_t max_ipc_in_flight_bytes;
} IT_RuntimeCapabilitiesV1;

uint32_t IT_CALL IT_EngineGetAbiVersion(void) IT_NOEXCEPT;

IT_Result IT_CALL IT_EngineGetCapabilities(
    IT_RuntimeCapabilitiesV1* capabilities) IT_NOEXCEPT;

IT_Result IT_CALL IT_EngineCreate(
    const IT_EngineCreateOptionsV1* options,
    IT_EngineHandle** engine) IT_NOEXCEPT;

IT_Result IT_CALL IT_EngineDestroy(IT_EngineHandle* engine) IT_NOEXCEPT;

#if defined(__cplusplus)
}
#endif

#undef IT_NOEXCEPT

#endif

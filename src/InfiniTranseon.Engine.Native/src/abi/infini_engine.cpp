#include <infini_engine.h>

uint32_t IT_CALL IT_EngineGetAbiVersion(void) noexcept
{
    return IT_ENGINE_ABI_VERSION;
}

IT_Result IT_CALL IT_EngineGetCapabilities(IT_RuntimeCapabilitiesV1* const capabilities) noexcept
{
    if (capabilities == nullptr)
    {
        return IT_RESULT_INVALID_ARGUMENT;
    }

    if (capabilities->struct_size < sizeof(IT_RuntimeCapabilitiesV1))
    {
        return IT_RESULT_INVALID_STRUCTURE_SIZE;
    }

    if (capabilities->abi_version != IT_ENGINE_ABI_VERSION)
    {
        return IT_RESULT_ABI_VERSION_MISMATCH;
    }

    capabilities->max_capture_sources = 8U;
    capabilities->max_targets = 8U;
    capabilities->max_capture_dimension = 8'192U;
    capabilities->max_capture_pixels_per_source = 33'554'432ULL;
    capabilities->max_regions_per_target = 256U;
    capabilities->max_active_tracks_per_target = 512U;
    capabilities->max_ocr_boxes_per_result = 2'048U;
    capabilities->max_source_chars = 4'096U;
    capabilities->max_overlay_chars_per_target = 16'384U;
    capabilities->max_translation_channels_per_region = 4U;
    capabilities->max_outstanding_wgc_frames_per_source = 3U;
    capabilities->max_owned_frame_textures_per_source = 2U;
    capabilities->max_readback_crops_per_source = 8U;
    capabilities->max_readback_pixels_per_source_ring = 8'388'608ULL;
    capabilities->max_global_ocr_crop_bytes_in_flight = 134'217'728ULL;
    capabilities->max_mapped_readbacks_per_adapter = 2U;
    capabilities->max_mapped_readback_hold_milliseconds = 20U;
    capabilities->max_detection_pyramid_bytes_per_source = 67'108'864ULL;
    capabilities->max_overlay_surface_bytes_per_target = 134'217'728ULL;
    capabilities->max_ocr_sessions = 4U;
    capabilities->max_ocr_tensor_workspace_bytes = 268'435'456ULL;
    capabilities->max_engine_committed_bytes = 2'147'483'648ULL;
    capabilities->max_gpu_bytes_per_adapter_ceiling = 1'073'741'824ULL;
    capabilities->max_gpu_budget_percentage = 25U;
    capabilities->max_ipc_message_bytes = 8'388'608U;
    capabilities->max_ipc_in_flight_bytes = 33'554'432ULL;
    return IT_RESULT_OK;
}

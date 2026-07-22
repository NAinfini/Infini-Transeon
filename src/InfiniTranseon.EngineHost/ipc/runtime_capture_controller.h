#pragma once

#include "infini_runtime_protocol.h"

#include <cstdint>
#include <cstddef>
#include <array>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace infini::runtime
{
[[nodiscard]] std::uint64_t calculate_gpu_pool_limit(
    std::uint64_t operating_system_budget) noexcept;
[[nodiscard]] bool runtime_capacity_available(
    std::size_t current,
    std::size_t requested,
    std::size_t limit) noexcept;

struct AdapterGpuBudget final
{
    std::uint64_t adapter_key{};
    std::uint64_t limit{};
    std::uint64_t committed{};
};

struct CaptureTargetApplyResult final
{
    bool accepted{};
    std::uint32_t lifecycle_state{};
    std::string error_code;
    std::int32_t native_error_code{};
};

struct CaptureTargetLifecycleEvent final
{
    std::array<std::byte, 16U> target_id{};
    std::array<std::byte, 16U> target_instance_id{};
    std::uint64_t lifecycle_sequence{};
    std::uint32_t lifecycle_state{};
    std::uint32_t pixel_width{};
    std::uint32_t pixel_height{};
    std::uint32_t dpi{};
    std::string error_code;
    std::int32_t native_error_code{};
};

struct OverlayApplyResult final
{
    bool accepted{};
    std::uint32_t status{};
    std::string error_code;
    std::int32_t native_error_code{};
};

struct ProcessingApplyResult final
{
    bool accepted{};
    std::uint8_t status{};
    std::string error_code;
};

struct OcrResultApplyResult final
{
    bool accepted{};
    std::uint32_t status{};
    std::string error_code;
};

class RuntimeCaptureController final
{
public:
    using lifecycle_callback = std::function<void(const CaptureTargetLifecycleEvent&)>;
    using cloud_ocr_callback = std::function<bool(const CloudOcrCropEvent&)>;
    using ocr_result_callback = std::function<bool(const OcrResultCommand&)>;

    RuntimeCaptureController(
        lifecycle_callback callback,
        std::array<std::byte, 16U> runtime_epoch,
        cloud_ocr_callback cloud_ocr,
        ocr_result_callback ocr_result);
    ~RuntimeCaptureController();
    RuntimeCaptureController(const RuntimeCaptureController&) = delete;
    RuntimeCaptureController& operator=(const RuntimeCaptureController&) = delete;

    [[nodiscard]] CaptureTargetApplyResult apply(const CaptureTargetCommand& command) noexcept;
    [[nodiscard]] OverlayApplyResult apply_overlay(
        std::shared_ptr<const overlay::desired_state> state) noexcept;
    [[nodiscard]] bool contains_target(
        const std::array<std::byte, 16U>& target_instance_id) const noexcept;
    [[nodiscard]] ProcessingApplyResult apply_processing(
        ProcessingConfigurationCommand command) noexcept;
    [[nodiscard]] std::string apply_policy(
        const PolicyRevisionCommand& command) noexcept;
    [[nodiscard]] OcrResultApplyResult apply_ocr_result(
        const OcrResultCommand& command) noexcept;
    [[nodiscard]] std::size_t target_count() const noexcept;
    [[nodiscard]] std::size_t capture_source_count() const noexcept;
    [[nodiscard]] bool try_get_gpu_budgets(
        std::vector<AdapterGpuBudget>& budgets) const noexcept;

private:
    struct Implementation;
    std::unique_ptr<Implementation> implementation_;
};
}

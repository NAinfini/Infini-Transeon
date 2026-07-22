#pragma once

#include <cstddef>
#include <optional>
#include <vector>

namespace infini::diagnostics {

struct performance_sample final {
    double process_cpu_percent{};
    std::size_t committed_bytes{};
    std::optional<double> gpu_time_ms;
    double ocr_latency_ms{};
    double translation_latency_ms{};
    double capture_frame_arrival_rate{};
    std::size_t queue_replacements{};
};

struct performance_summary final {
    std::size_t sample_count{};
    double cpu_p50{};
    double cpu_p95{};
    std::size_t committed_bytes_peak{};
    std::optional<double> gpu_time_p95;
    double ocr_latency_p95{};
    double translation_latency_p95{};
    double capture_frame_arrival_rate_p50{};
    std::size_t queue_replacements_total{};
};

class performance_sampler final {
public:
    explicit performance_sampler(std::size_t capacity = 600U);
    void push(performance_sample sample) noexcept;
    [[nodiscard]] performance_summary summarize() const;
    [[nodiscard]] std::size_t size() const noexcept;

private:
    std::vector<performance_sample> samples_;
    std::size_t cursor_{};
    std::size_t count_{};
};

} // namespace infini::diagnostics

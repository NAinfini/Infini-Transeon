#include "infini/diagnostics/performance_sampler.hpp"

#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace infini::diagnostics {
namespace {

[[nodiscard]] double percentile(std::vector<double> values, const double fraction) {
    if (values.empty()) return 0.0;
    std::ranges::sort(values);
    const auto index = static_cast<std::size_t>(
        std::ceil(fraction * static_cast<double>(values.size())) - 1.0);
    return values[std::min(index, values.size() - 1U)];
}

} // namespace

performance_sampler::performance_sampler(const std::size_t capacity) : samples_(capacity) {
    if (capacity == 0U || capacity > 3600U) throw std::invalid_argument("invalid sampler capacity");
}

void performance_sampler::push(const performance_sample sample) noexcept {
    samples_[cursor_] = sample;
    cursor_ = (cursor_ + 1U) % samples_.size();
    count_ = std::min(count_ + 1U, samples_.size());
}

performance_summary performance_sampler::summarize() const {
    std::vector<double> cpu;
    std::vector<double> gpu;
    std::vector<double> ocr;
    std::vector<double> translation;
    std::vector<double> arrivals;
    cpu.reserve(count_);
    gpu.reserve(count_);
    ocr.reserve(count_);
    translation.reserve(count_);
    arrivals.reserve(count_);
    std::size_t peak{};
    std::size_t replacements{};
    for (std::size_t index = 0; index < count_; ++index) {
        const auto& sample = samples_[index];
        cpu.push_back(sample.process_cpu_percent);
        if (sample.gpu_time_ms.has_value()) gpu.push_back(*sample.gpu_time_ms);
        ocr.push_back(sample.ocr_latency_ms);
        translation.push_back(sample.translation_latency_ms);
        arrivals.push_back(sample.capture_frame_arrival_rate);
        peak = std::max(peak, sample.committed_bytes);
        replacements += sample.queue_replacements;
    }
    return performance_summary{
        count_, percentile(cpu, 0.50), percentile(cpu, 0.95), peak,
        gpu.empty() ? std::nullopt : std::optional<double>{percentile(gpu, 0.95)},
        percentile(ocr, 0.95), percentile(translation, 0.95),
        percentile(arrivals, 0.50), replacements,
    };
}

std::size_t performance_sampler::size() const noexcept { return count_; }

} // namespace infini::diagnostics

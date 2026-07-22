#pragma once

#include <cstddef>
#include <map>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <utility>

namespace infini::scheduling
{
enum class latest_push_status
{
    accepted,
    replaced,
    capacity_rejected,
};

template<typename Value>
struct latest_push_result
{
    latest_push_status status{latest_push_status::capacity_rejected};
    std::optional<Value> displaced{};
};

template<typename Key, typename Value>
class latest_queue final
{
public:
    explicit latest_queue(const std::size_t maximum_keys) : maximum_keys_(maximum_keys)
    {
        if (maximum_keys_ == 0U)
        {
            throw std::invalid_argument("latest_queue capacity must be positive");
        }
    }

    latest_push_result<Value> push(Key key, Value value)
    {
        std::scoped_lock lock(mutex_);
        const auto existing = values_.find(key);
        if (existing != values_.end())
        {
            Value displaced = std::move(existing->second);
            existing->second = std::move(value);
            return {latest_push_status::replaced, std::move(displaced)};
        }
        if (values_.size() >= maximum_keys_)
        {
            return {};
        }
        values_.emplace(std::move(key), std::move(value));
        return {latest_push_status::accepted, std::nullopt};
    }

    std::optional<Value> take(const Key& key)
    {
        std::scoped_lock lock(mutex_);
        const auto existing = values_.find(key);
        if (existing == values_.end()) return std::nullopt;
        Value value = std::move(existing->second);
        values_.erase(existing);
        return value;
    }

    bool cancel(const Key& key)
    {
        std::scoped_lock lock(mutex_);
        return values_.erase(key) != 0U;
    }

    [[nodiscard]] std::size_t size() const
    {
        std::scoped_lock lock(mutex_);
        return values_.size();
    }

private:
    std::size_t maximum_keys_{};
    mutable std::mutex mutex_{};
    std::map<Key, Value> values_{};
};
}

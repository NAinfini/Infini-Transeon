#include <infini/ocr/readback_ring.hpp>

#include <stdexcept>
#include <utility>

namespace infini::ocr
{
mapped_readback_lease::mapped_readback_lease(
    readback_ring* const owner,
    const readback_ticket ticket,
    const std::size_t byte_count,
    const readback_clock::time_point mapped_at,
    const std::chrono::milliseconds maximum_hold) noexcept
    : owner_(owner),
      ticket_(ticket),
      byte_count_(byte_count),
      mapped_at_(mapped_at),
      maximum_hold_(maximum_hold)
{
}

mapped_readback_lease::mapped_readback_lease(mapped_readback_lease&& other) noexcept
    : owner_(std::exchange(other.owner_, nullptr)),
      ticket_(other.ticket_),
      byte_count_(other.byte_count_),
      mapped_at_(other.mapped_at_),
      maximum_hold_(other.maximum_hold_)
{
}

mapped_readback_lease& mapped_readback_lease::operator=(mapped_readback_lease&& other) noexcept
{
    if (this != &other)
    {
        release();
        owner_ = std::exchange(other.owner_, nullptr);
        ticket_ = other.ticket_;
        byte_count_ = other.byte_count_;
        mapped_at_ = other.mapped_at_;
        maximum_hold_ = other.maximum_hold_;
    }
    return *this;
}

mapped_readback_lease::~mapped_readback_lease()
{
    release();
}

bool mapped_readback_lease::within_hold_limit(const readback_clock::time_point now) const noexcept
{
    return owner_ != nullptr && now - mapped_at_ <= maximum_hold_;
}

void mapped_readback_lease::release() noexcept
{
    if (owner_ != nullptr)
    {
        owner_->release_mapped(ticket_);
        owner_ = nullptr;
    }
}

readback_ring::readback_ring(
    const std::size_t maximum_slots,
    const std::size_t maximum_bytes,
    const std::size_t maximum_mapped,
    const std::size_t maximum_copy_bytes_per_dispatch,
    const std::chrono::milliseconds maximum_hold)
    : maximum_slots_(maximum_slots),
      maximum_bytes_(maximum_bytes),
      maximum_mapped_(maximum_mapped),
      maximum_copy_bytes_per_dispatch_(maximum_copy_bytes_per_dispatch),
      maximum_hold_(maximum_hold)
{
    if (maximum_slots_ == 0U || maximum_bytes_ == 0U || maximum_mapped_ == 0U ||
        maximum_mapped_ > maximum_slots_ || maximum_copy_bytes_per_dispatch_ == 0U ||
        maximum_hold_ <= std::chrono::milliseconds::zero())
    {
        throw std::invalid_argument("readback ring configuration is invalid");
    }
}

std::optional<readback_ticket> readback_ring::reserve(const std::size_t byte_count)
{
    std::scoped_lock lock(mutex_);
    if (byte_count == 0U || byte_count > maximum_copy_bytes_per_dispatch_ ||
        slots_.size() >= maximum_slots_ || byte_count > maximum_bytes_ - committed_bytes_)
    {
        return std::nullopt;
    }
    const readback_ticket ticket = next_ticket_++;
    if (next_ticket_ == 0U) ++next_ticket_;
    slots_.emplace(ticket, slot_state{byte_count, false, false});
    committed_bytes_ += byte_count;
    return ticket;
}

void readback_ring::mark_fence_complete(const readback_ticket ticket)
{
    std::scoped_lock lock(mutex_);
    const auto slot = slots_.find(ticket);
    if (slot == slots_.end()) throw std::invalid_argument("readback ticket is unknown");
    slot->second.fence_complete = true;
}

std::optional<mapped_readback_lease> readback_ring::try_map(
    const readback_ticket ticket,
    const readback_clock::time_point now)
{
    std::scoped_lock lock(mutex_);
    const auto slot = slots_.find(ticket);
    if (slot == slots_.end() || !slot->second.fence_complete || slot->second.mapped ||
        mapped_count_ >= maximum_mapped_)
    {
        return std::nullopt;
    }
    slot->second.mapped = true;
    ++mapped_count_;
    return mapped_readback_lease(this, ticket, slot->second.byte_count, now, maximum_hold_);
}

bool readback_ring::cancel(const readback_ticket ticket)
{
    std::scoped_lock lock(mutex_);
    const auto slot = slots_.find(ticket);
    if (slot == slots_.end() || slot->second.mapped) return false;
    committed_bytes_ -= slot->second.byte_count;
    slots_.erase(slot);
    return true;
}

void readback_ring::release_mapped(const readback_ticket ticket) noexcept
{
    std::scoped_lock lock(mutex_);
    const auto slot = slots_.find(ticket);
    if (slot == slots_.end() || !slot->second.mapped) return;
    committed_bytes_ -= slot->second.byte_count;
    if (mapped_count_ > 0U) --mapped_count_;
    slots_.erase(slot);
}
}

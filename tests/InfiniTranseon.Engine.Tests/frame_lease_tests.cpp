#include "infini/capture/frame_lease.hpp"

#include <cassert>

int main() {
    using namespace infini::capture;
    int closes = 0;
    frame_lease lease({1U, 2U, 3U, 4U}, [&closes] { ++closes; });
    auto cancelled = lease.acquire_ticket();
    auto submitted = lease.acquire_ticket();
    assert(submitted.submit());
    lease.release_root();
    assert(closes == 0);
    assert(cancelled.cancel());
    assert(closes == 0);
    assert(submitted.complete());
    assert(closes == 1);
    assert(!submitted.complete());

    crop_lease crop{{1U, 2U, 3U, 4U}, 5U, 2U, 2U, 8U, std::vector<std::byte>(16U)};
    assert(crop.valid(16U));
    assert(!crop.valid(15U));
    return 0;
}

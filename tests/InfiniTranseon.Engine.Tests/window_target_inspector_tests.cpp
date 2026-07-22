#include "infini/capture/window_target_inspector.hpp"

#include <cassert>

int main() {
    const auto missing = infini::capture::inspect_window_target(nullptr);
    assert(missing.status == infini::capture::window_inspection_status::closed);
    assert(!missing.snapshot.has_value());
    return 0;
}

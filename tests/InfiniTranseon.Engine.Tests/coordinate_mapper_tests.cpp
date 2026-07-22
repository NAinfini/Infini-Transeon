#include <cstdlib>

#include <infini/imaging/coordinate_mapper.hpp>

namespace
{
void require(const bool condition)
{
    if (!condition)
    {
        std::abort();
    }
}
}

int main()
{
    using namespace infini::imaging;

    const content_space standard{{-1920, 100}, {1920, 1080}, 96U};
    const mapped_region mapped = map_region(
        {0.1, 0.2, 0.5, 0.25}, standard, display_rotation::identity, {2, 2});
    require(mapped.status == mapping_status::ok);
    require(mapped.source == pixel_rect{192, 216, 960, 270});
    require(mapped.overlay == pixel_rect{-1728, 316, 960, 270});

    const content_space mixed_dpi{{1920, -200}, {3840, 2160}, 192U};
    const mapped_region high_dpi = map_region(
        {0.1, 0.2, 0.5, 0.25}, mixed_dpi, display_rotation::identity, {2, 2});
    require(high_dpi.source == pixel_rect{384, 432, 1920, 540});
    require(high_dpi.overlay == pixel_rect{2304, 232, 1920, 540});

    const content_space three_hundred_percent{{0, 0}, {5760, 3240}, 288U};
    const mapped_region scaled = map_region(
        {0.25, 0.25, 0.5, 0.5}, three_hundred_percent, display_rotation::identity, {2, 2});
    require(scaled.source == pixel_rect{1440, 810, 2880, 1620});

    const mapped_region one_pixel = map_region(
        {0.0001, 0.0001, 0.0001, 0.0001}, {{0, 0}, {1000, 1000}, 96U},
        display_rotation::identity, {2, 2});
    require(one_pixel.status == mapping_status::below_minimum_size);
    require(one_pixel.source == pixel_rect{0, 0, 1, 1});

    const mapped_region rotated = map_region(
        {0.0, 0.0, 0.5, 0.25}, {{0, 0}, {1080, 1920}, 144U},
        display_rotation::clockwise_90, {2, 2});
    require(rotated.source == pixel_rect{810, 0, 270, 960});

    const mapped_region invalid = map_region(
        {-0.1, 0.0, 0.5, 0.5}, standard, display_rotation::identity, {2, 2});
    require(invalid.status == mapping_status::invalid_input);
    require(invalid.source == pixel_rect{});

    return EXIT_SUCCESS;
}

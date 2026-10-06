#include <ebur128.h>

#include <cmath>
#include <cstddef>
#include <iostream>
#include <vector>

int main()
{
    constexpr unsigned int channels = 2;
    constexpr unsigned long sampleRate = 48'000;
    constexpr std::size_t frames = sampleRate;

    auto* state = ebur128_init(channels, sampleRate, EBUR128_MODE_I);
    if (state == nullptr)
    {
        std::cerr << "ebur128_init failed\n";
        return 1;
    }

    const std::vector<float> silence(frames * channels, 0.0f);
    const auto addResult = ebur128_add_frames_float(state, silence.data(), frames);

    double loudness = 0.0;
    const auto loudnessResult = ebur128_loudness_global(state, &loudness);
    ebur128_destroy(&state);

    if (addResult != EBUR128_SUCCESS || loudnessResult != EBUR128_SUCCESS)
    {
        std::cerr << "libebur128 API returned an error\n";
        return 2;
    }

    if (!std::isinf(loudness) || loudness >= 0.0)
    {
        std::cerr << "Digital silence did not return negative infinity\n";
        return 3;
    }

    std::cout << "libebur128 1.2.6 static-link smoke test passed\n";
    return 0;
}

#pragma once

#include <cstdint>
#include <cstring>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>

struct WavData
{
    unsigned int channels {};
    unsigned long sampleRate {};
    std::vector<float> interleaved;
    [[nodiscard]] std::size_t frames() const noexcept { return interleaved.size() / channels; }
};

inline std::uint16_t readU16(std::istream& input)
{
    unsigned char bytes[2] {};
    input.read(reinterpret_cast<char*>(bytes), 2);
    return static_cast<std::uint16_t>(bytes[0] | (bytes[1] << 8));
}

inline std::uint32_t readU32(std::istream& input)
{
    unsigned char bytes[4] {};
    input.read(reinterpret_cast<char*>(bytes), 4);
    return static_cast<std::uint32_t>(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
}

inline WavData readWav(const std::string& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input)
        throw std::runtime_error("Cannot open WAV: " + path);
    char id[4] {};
    input.read(id, 4);
    if (std::memcmp(id, "RIFF", 4) != 0)
        throw std::runtime_error("Not RIFF: " + path);
    (void) readU32(input);
    input.read(id, 4);
    if (std::memcmp(id, "WAVE", 4) != 0)
        throw std::runtime_error("Not WAVE: " + path);

    std::uint16_t format = 0;
    std::uint16_t bits = 0;
    WavData result;
    std::vector<unsigned char> bytes;
    while (input && (format == 0 || bytes.empty()))
    {
        input.read(id, 4);
        if (!input) break;
        const auto size = readU32(input);
        if (std::memcmp(id, "fmt ", 4) == 0)
        {
            format = readU16(input);
            result.channels = readU16(input);
            result.sampleRate = readU32(input);
            (void) readU32(input);
            (void) readU16(input);
            bits = readU16(input);
            if (size > 16)
            {
                const auto extraSize = readU16(input);
                if (format == 0xfffe && extraSize >= 22)
                {
                    (void) readU16(input);
                    (void) readU32(input);
                    const auto subFormat = readU16(input);
                    format = subFormat;
                    input.seekg(static_cast<std::streamoff>(extraSize - 8), std::ios::cur);
                }
                else if (extraSize > 0)
                {
                    input.seekg(static_cast<std::streamoff>(extraSize), std::ios::cur);
                }
            }
        }
        else if (std::memcmp(id, "data", 4) == 0)
        {
            bytes.resize(size);
            input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(size));
        }
        else
        {
            input.seekg(static_cast<std::streamoff>(size), std::ios::cur);
        }
        if ((size & 1U) != 0U)
            input.seekg(1, std::ios::cur);
    }

    if (result.channels == 0 || result.sampleRate == 0 || bytes.empty())
        throw std::runtime_error("Incomplete WAV: " + path);
    const std::size_t sampleBytes = bits / 8;
    if (sampleBytes == 0 || bytes.size() % sampleBytes != 0)
        throw std::runtime_error("Unsupported WAV alignment: " + path);
    result.interleaved.resize(bytes.size() / sampleBytes);
    for (std::size_t i = 0; i < result.interleaved.size(); ++i)
    {
        const auto* p = bytes.data() + i * sampleBytes;
        if (format == 1 && bits == 16)
        {
            const auto value = static_cast<std::int16_t>(p[0] | (p[1] << 8));
            result.interleaved[i] = static_cast<float>(value) / 32768.0f;
        }
        else if (format == 1 && bits == 24)
        {
            std::int32_t value = static_cast<std::int32_t>(p[0] | (p[1] << 8) | (p[2] << 16));
            if ((value & 0x00800000) != 0)
                value |= static_cast<std::int32_t>(0xff000000);
            result.interleaved[i] = static_cast<float>(value) / 8388608.0f;
        }
        else if (format == 1 && bits == 32)
        {
            const auto value = static_cast<std::int32_t>(p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24));
            result.interleaved[i] = static_cast<float>(static_cast<double>(value) / 2147483648.0);
        }
        else if (format == 3 && bits == 32)
        {
            float value {};
            std::memcpy(&value, p, sizeof(value));
            result.interleaved[i] = value;
        }
        else
        {
            throw std::runtime_error("Unsupported WAV encoding: " + path);
        }
    }
    return result;
}

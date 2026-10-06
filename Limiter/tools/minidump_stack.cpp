#define NOMINMAX
#include <windows.h>
#include <dbghelp.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <cstdint>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <string>
#include <vector>

namespace
{
struct DumpRange
{
    std::uint64_t address;
    std::uint64_t size;
    std::uint64_t rva;
};

struct ModuleRange
{
    std::uint64_t begin;
    std::uint64_t end;
};

std::byte* dumpBase = nullptr;
std::vector<DumpRange> dumpRanges;

BOOL CALLBACK readDumpMemory(HANDLE, DWORD64 address, PVOID destination,
                             DWORD size, LPDWORD bytesRead)
{
    for (const auto& range : dumpRanges)
    {
        if (address < range.address || address >= range.address + range.size)
            continue;
        const auto available = range.address + range.size - address;
        const auto count = static_cast<DWORD>(std::min<std::uint64_t>(size, available));
        std::memcpy(destination,
                    dumpBase + range.rva + (address - range.address), count);
        *bytesRead = count;
        return TRUE;
    }
    *bytesRead = 0;
    return FALSE;
}

std::wstring dumpString(void* base, RVA rva)
{
    if (rva == 0)
        return {};
    const auto* value = reinterpret_cast<const MINIDUMP_STRING*>(
        static_cast<const std::byte*>(base) + rva);
    return { value->Buffer, value->Length / sizeof(wchar_t) };
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2)
    {
        std::wcerr << L"usage: minidump_stack <dump-file>\n";
        return 2;
    }

    HANDLE file = CreateFileW(argv[1], GENERIC_READ, FILE_SHARE_READ, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return 3;
    HANDLE mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
    if (mapping == nullptr)
        return 4;
    void* base = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
    if (base == nullptr)
        return 5;
    dumpBase = static_cast<std::byte*>(base);

    MINIDUMP_DIRECTORY* directory = nullptr;
    void* stream = nullptr;
    ULONG streamSize = 0;
    if (!MiniDumpReadDumpStream(base, ModuleListStream, &directory, &stream, &streamSize))
        return 6;
    const auto* modules = static_cast<const MINIDUMP_MODULE_LIST*>(stream);
    std::vector<ModuleRange> moduleRanges;

    HANDLE process = GetCurrentProcess();
    SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME | SYMOPT_LOAD_LINES);
    if (!SymInitializeW(process, nullptr, FALSE))
        return 7;

    for (ULONG i = 0; i < modules->NumberOfModules; ++i)
    {
        const auto& module = modules->Modules[i];
        moduleRanges.push_back({ module.BaseOfImage,
                                 module.BaseOfImage + module.SizeOfImage });
        const auto path = dumpString(base, module.ModuleNameRva);
        const auto loaded = SymLoadModuleExW(process, nullptr, path.c_str(), nullptr,
                                             module.BaseOfImage,
                                             static_cast<DWORD>(module.SizeOfImage),
                                             nullptr, 0);
        if (path.find(L"HipHopMax") != std::wstring::npos)
            std::wcout << L"Plugin module 0x" << std::hex << module.BaseOfImage
                       << L"-0x" << (module.BaseOfImage + module.SizeOfImage)
                       << std::dec
                       << (loaded == 0 ? L" [symbols unavailable]" : L"") << L"\n";
    }

    if (MiniDumpReadDumpStream(base, Memory64ListStream, &directory, &stream, &streamSize))
    {
        const auto* memory = static_cast<const MINIDUMP_MEMORY64_LIST*>(stream);
        std::uint64_t rva = memory->BaseRva;
        for (ULONG64 i = 0; i < memory->NumberOfMemoryRanges; ++i)
        {
            dumpRanges.push_back({ memory->MemoryRanges[i].StartOfMemoryRange,
                                   memory->MemoryRanges[i].DataSize, rva });
            rva += memory->MemoryRanges[i].DataSize;
        }
    }
    else if (MiniDumpReadDumpStream(base, MemoryListStream, &directory, &stream, &streamSize))
    {
        const auto* memory = static_cast<const MINIDUMP_MEMORY_LIST*>(stream);
        for (ULONG i = 0; i < memory->NumberOfMemoryRanges; ++i)
            dumpRanges.push_back({ memory->MemoryRanges[i].StartOfMemoryRange,
                                   memory->MemoryRanges[i].Memory.DataSize,
                                   memory->MemoryRanges[i].Memory.Rva });
    }

    if (!MiniDumpReadDumpStream(base, ExceptionStream, &directory, &stream, &streamSize))
        return 8;
    const auto* exception = static_cast<const MINIDUMP_EXCEPTION_STREAM*>(stream);
    const auto* context = reinterpret_cast<const CONTEXT*>(
        static_cast<const std::byte*>(base) + exception->ThreadContext.Rva);

    std::wcout << L"Exception thread=" << exception->ThreadId
               << L" code=0x" << std::hex << exception->ExceptionRecord.ExceptionCode
               << L" address=0x" << exception->ExceptionRecord.ExceptionAddress
               << L" rip=0x" << context->Rip << L" rsp=0x" << context->Rsp
               << std::dec << L" memory-ranges=" << dumpRanges.size() << L"\n";

    CONTEXT mutableContext = *context;
    STACKFRAME64 frame {};
    frame.AddrPC.Offset = mutableContext.Rip;
    frame.AddrPC.Mode = AddrModeFlat;
    frame.AddrFrame.Offset = mutableContext.Rbp;
    frame.AddrFrame.Mode = AddrModeFlat;
    frame.AddrStack.Offset = mutableContext.Rsp;
    frame.AddrStack.Mode = AddrModeFlat;

    std::byte symbolStorage[sizeof(SYMBOL_INFOW) + MAX_SYM_NAME * sizeof(wchar_t)] {};
    auto* symbol = reinterpret_cast<SYMBOL_INFOW*>(symbolStorage);
    symbol->SizeOfStruct = sizeof(SYMBOL_INFOW);
    symbol->MaxNameLen = MAX_SYM_NAME;

    std::wcout << L"Return-address candidates near RSP:\n";
    std::array<std::uint64_t, 512> stackWords {};
    DWORD stackBytesRead = 0;
    readDumpMemory(nullptr, mutableContext.Rsp, stackWords.data(),
                   static_cast<DWORD>(sizeof(stackWords)), &stackBytesRead);
    for (DWORD i = 0; i < stackBytesRead / sizeof(stackWords[0]); ++i)
    {
        const auto address = stackWords[i];
        const auto inModule = std::any_of(moduleRanges.begin(), moduleRanges.end(),
                                          [address](const auto& module)
                                          {
                                              return address >= module.begin && address < module.end;
                                          });
        if (!inModule)
            continue;
        DWORD64 displacement = 0;
        std::wcout << L"  rsp+0x" << std::hex << (i * sizeof(stackWords[0]))
                   << L" 0x" << address << std::dec;
        if (SymFromAddrW(process, address, &displacement, symbol))
            std::wcout << L" " << symbol->Name << L"+0x" << std::hex
                       << displacement << std::dec;
        std::wcout << L"\n";
    }

    std::wcout << L"Unwound stack:\n";

    for (int depth = 0; depth < 80; ++depth)
    {
        if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, nullptr, &frame,
                         &mutableContext, readDumpMemory, SymFunctionTableAccess64,
                         SymGetModuleBase64, nullptr))
            break;
        if (frame.AddrPC.Offset == 0)
            break;

        DWORD64 displacement = 0;
        std::wcout << std::setw(2) << depth << L" 0x" << std::hex
                   << frame.AddrPC.Offset << std::dec;
        if (SymFromAddrW(process, frame.AddrPC.Offset, &displacement, symbol))
            std::wcout << L" " << symbol->Name << L"+0x" << std::hex
                       << displacement << std::dec;

        IMAGEHLP_LINEW64 line {};
        line.SizeOfStruct = sizeof(line);
        DWORD lineDisplacement = 0;
        if (SymGetLineFromAddrW64(process, frame.AddrPC.Offset, &lineDisplacement, &line))
            std::wcout << L" (" << line.FileName << L":" << line.LineNumber << L")";
        std::wcout << L"\n";
    }

    SymCleanup(process);
    UnmapViewOfFile(base);
    CloseHandle(mapping);
    CloseHandle(file);
    return 0;
}

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cassert>
#include <cstdio>

int main() {
    HMODULE real = LoadLibraryW(L"winmm_real.dll");
    HMODULE proxy = LoadLibraryW(L"winmm.dll");
    assert(real && proxy && real != proxy);
    auto base = reinterpret_cast<const BYTE*>(proxy);
    auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    auto exports = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress);
    auto names = reinterpret_cast<const DWORD*>(base + exports->AddressOfNames);
    auto ordinals = reinterpret_cast<const WORD*>(base + exports->AddressOfNameOrdinals);
    assert(exports->NumberOfNames == 180);
    for (DWORD i = 0; i < exports->NumberOfNames; ++i) {
        const char* name = reinterpret_cast<const char*>(base + names[i]);
        auto target = GetProcAddress(real, name);
        assert(target && GetProcAddress(proxy, name) == target);
        assert(GetProcAddress(proxy, MAKEINTRESOURCEA(exports->Base + ordinals[i])) == target);
    }
    assert(GetProcAddress(proxy, MAKEINTRESOURCEA(2)) == GetProcAddress(real, MAKEINTRESOURCEA(2)));
#pragma warning(suppress: 4191)
    auto timeGetTime = reinterpret_cast<DWORD(WINAPI*)()>(GetProcAddress(proxy, "timeGetTime"));
    DWORD before = timeGetTime();
    Sleep(50);
    assert(timeGetTime() - before >= 1);
    puts("All 181 native audio exports forward correctly; timeGetTime executed successfully.");
    // Let process teardown unload modules after the short non-Steam worker has returned.
}

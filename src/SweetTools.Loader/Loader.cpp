#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <cwchar>
#include "LaunchPolicy.h"

namespace {
constexpr wchar_t ConfigKey[] = L"Software\\SweetTools\\SteamLauncher";
constexpr wchar_t SteamKey[] = L"Software\\Valve\\Steam";

struct Key {
    HKEY value = nullptr;
    ~Key() { if (value) RegCloseKey(value); }
};
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value) CloseHandle(value); }
};

std::wstring ReadString(HKEY key, const wchar_t* name) {
    wchar_t buffer[32768];
    DWORD bytes = sizeof(buffer);
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ, nullptr, buffer, &bytes) != ERROR_SUCCESS)
        return {};
    return buffer;
}

DWORD ReadDword(HKEY key, const wchar_t* name) {
    DWORD value = 0, bytes = sizeof(value);
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_DWORD, nullptr, &value, &bytes) != ERROR_SUCCESS)
        return 0;
    return value;
}

bool ParseConfig(const std::wstring& text, std::wstring& exe, std::wstring& steamExe, uint64_t& allowed) {
    const auto first = text.find(L'\n'), last = text.rfind(L'\n');
    if (first == std::wstring::npos || first == last) return false;
    exe = text.substr(0, first);
    steamExe = text.substr(first + 1, last - first - 1);
    const auto id = text.substr(last + 1);
    if (exe.empty() || steamExe.empty() || exe.find(L'"') != std::wstring::npos ||
        id.size() != 17 || id.find_first_not_of(L"0123456789") != std::wstring::npos) return false;
    allowed = _wcstoui64(id.c_str(), nullptr, 10);
    return allowed > 76561197960265728ULL && allowed <= 76561197960265728ULL + UINT32_MAX;
}

bool Launch(const std::wstring& exe) {
    // Explicit application path; do not invoke a shell or interpret a protocol command.
    std::wstring command = L"\"" + exe + L"\" --steam-account-launch";
    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &startup, &process)) return false;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return true;
}

DWORD WINAPI Monitor(void*) {
    try {
        wchar_t host[32768];
        DWORD size = GetModuleFileNameW(nullptr, host, ARRAYSIZE(host));
        if (!size || size == ARRAYSIZE(host)) return 0;
#ifndef SWEETTOOLS_LOADER_TEST
        const wchar_t* basename = wcsrchr(host, L'\\');
        if (!basename || _wcsicmp(basename + 1, L"steam.exe") != 0) return 0;
#endif

        // Steam owns this module for its lifetime. Pin before registering callbacks/waits.
        HMODULE self;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&Monitor), &self)) return 0;

        Key config, steam;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, ConfigKey, 0, KEY_READ, &config.value) != ERROR_SUCCESS ||
            RegOpenKeyExW(HKEY_CURRENT_USER, SteamKey, 0, KEY_READ, &steam.value) != ERROR_SUCCESS) return 0;
        Handle configEvent{ CreateEventW(nullptr, FALSE, FALSE, nullptr) };
        Handle steamEvent{ CreateEventW(nullptr, FALSE, FALSE, nullptr) };
        if (!configEvent.value || !steamEvent.value) return 0;
        HANDLE events[]{ configEvent.value, steamEvent.value };
        constexpr DWORD filter = REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET;
        // Watch Steam's parent key so creation/replacement of ActiveProcess is also detected.
        if (RegNotifyChangeKeyValue(config.value, FALSE, filter, events[0], TRUE) != ERROR_SUCCESS ||
            RegNotifyChangeKeyValue(steam.value, TRUE, filter, events[1], TRUE) != ERROR_SUCCESS) return 0;

        LaunchPolicy policy;
        for (;;) {
            std::wstring exe, steamExe;
            uint64_t allowed = 0;
            const bool configured = ParseConfig(ReadString(config.value, L"Configuration"), exe, steamExe, allowed);
            Key active;
            DWORD account = 0, pid = 0;
            if (RegOpenKeyExW(steam.value, L"ActiveProcess", 0, KEY_QUERY_VALUE, &active.value) == ERROR_SUCCESS) {
                account = ReadDword(active.value, L"ActiveUser");
                pid = ReadDword(active.value, L"pid");
            }
            bool sameSteam = configured && pid == GetCurrentProcessId() && _wcsicmp(steamExe.c_str(), host) == 0;
            if (policy.ShouldLaunch(allowed, account, sameSteam) && Launch(exe)) policy.MarkLaunched(account);

            // No timeout or polling. Rearm only the notification that fired, before reading again.
            DWORD result = WaitForMultipleObjects(2, events, FALSE, INFINITE);
            if (result > WAIT_OBJECT_0 + 1) return 0;
            DWORD index = result - WAIT_OBJECT_0;
            HKEY changed = index == 0 ? config.value : steam.value;
            if (RegNotifyChangeKeyValue(changed, index == 1, filter, events[index], TRUE) != ERROR_SUCCESS) return 0;
        }
    } catch (...) {
        // A launcher failure must never bring down Steam or affect forwarded audio calls.
        return 0;
    }
}
}

#ifndef SWEETTOOLS_LOADER_TEST
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        // All registry and process work runs after the loader lock has been released.
        HANDLE thread = CreateThread(nullptr, 0, Monitor, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
#endif

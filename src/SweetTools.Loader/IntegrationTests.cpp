#define SWEETTOOLS_LOADER_TEST
#include "Loader.cpp"
#include <cassert>
#include <cstdio>

void SetText(HKEY key, const wchar_t* name, const std::wstring& value) {
    assert(RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS);
}
void SetNumber(HKEY key, const wchar_t* name, DWORD value) {
    assert(RegSetValueExW(key, name, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value)) == ERROR_SUCCESS);
}

int wmain(int argc, wchar_t**) {
    wchar_t eventName[128];
    if (argc > 1) {
        assert(GetEnvironmentVariableW(L"SWEETTOOLS_TEST_EVENT", eventName, ARRAYSIZE(eventName)));
        Handle event{ OpenEventW(EVENT_MODIFY_STATE, FALSE, eventName) };
        assert(event.value && SetEvent(event.value));
        return 0;
    }
    // Process-local HKCU override: production Steam/settings keys are never touched.
    std::wstring root = L"Software\\SweetToolsLoaderTest-" + std::to_wstring(GetCurrentProcessId());
    Key sandbox;
    assert(RegCreateKeyExW(HKEY_CURRENT_USER, root.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &sandbox.value, nullptr) == ERROR_SUCCESS);
    assert(RegOverridePredefKey(HKEY_CURRENT_USER, sandbox.value) == ERROR_SUCCESS);
    {
        Key config, steam, active;
        assert(RegCreateKeyExW(HKEY_CURRENT_USER, ConfigKey, 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &config.value, nullptr) == ERROR_SUCCESS);
        assert(RegCreateKeyExW(HKEY_CURRENT_USER, SteamKey, 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &steam.value, nullptr) == ERROR_SUCCESS);
        assert(RegCreateKeyExW(steam.value, L"ActiveProcess", 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &active.value, nullptr) == ERROR_SUCCESS);
        wchar_t exe[32768];
        assert(GetModuleFileNameW(nullptr, exe, ARRAYSIZE(exe)));
        const std::wstring configuration = std::wstring(exe) + L"\n" + exe + L"\n76561197960278073";
        SetText(config.value, L"Configuration", configuration);
        SetNumber(active.value, L"pid", GetCurrentProcessId());
        SetNumber(active.value, L"ActiveUser", 54321);
        std::wstring name = L"Local\\SweetToolsLoaderTest-" + std::to_wstring(GetCurrentProcessId());
        assert(SetEnvironmentVariableW(L"SWEETTOOLS_TEST_EVENT", name.c_str()));
        Handle launched{ CreateEventW(nullptr, FALSE, FALSE, name.c_str()) };
        Handle thread{ CreateThread(nullptr, 0, Monitor, nullptr, 0, nullptr) };
        assert(thread.value);
        assert(WaitForSingleObject(launched.value, 300) == WAIT_TIMEOUT);
        SetNumber(active.value, L"ActiveUser", 12345);
        assert(WaitForSingleObject(launched.value, 5000) == WAIT_OBJECT_0);
        SetNumber(active.value, L"ActiveUser", 12345);
        assert(WaitForSingleObject(launched.value, 300) == WAIT_TIMEOUT);
        SetNumber(active.value, L"ActiveUser", 0);
        assert(WaitForSingleObject(launched.value, 300) == WAIT_TIMEOUT);
        SetNumber(active.value, L"ActiveUser", 12345);
        assert(WaitForSingleObject(launched.value, 5000) == WAIT_OBJECT_0);
        SetText(config.value, L"Configuration", L"");
        assert(WaitForSingleObject(launched.value, 300) == WAIT_TIMEOUT);
        SetText(config.value, L"Configuration", configuration);
        assert(WaitForSingleObject(launched.value, 5000) == WAIT_OBJECT_0);
        SetNumber(active.value, L"pid", GetCurrentProcessId() + 1);
        assert(WaitForSingleObject(launched.value, 300) == WAIT_TIMEOUT);
        SetNumber(active.value, L"pid", GetCurrentProcessId());
        assert(WaitForSingleObject(launched.value, 5000) == WAIT_OBJECT_0);
        assert(RegDeleteKeyW(HKEY_CURRENT_USER, ConfigKey) == ERROR_SUCCESS);
        assert(WaitForSingleObject(thread.value, 5000) == WAIT_OBJECT_0);
    }
    assert(RegOverridePredefKey(HKEY_CURRENT_USER, nullptr) == ERROR_SUCCESS);
    assert(RegDeleteTreeW(HKEY_CURRENT_USER, root.c_str()) == ERROR_SUCCESS);
    puts("Native registry notification and launch integration tests passed.");
}

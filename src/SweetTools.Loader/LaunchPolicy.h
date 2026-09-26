#pragma once
#include <cstdint>

// Kept free of Win32 calls so account/session transitions can be tested directly.
struct LaunchPolicy {
    uint32_t launchedAccount = 0;
    bool ShouldLaunch(uint64_t allowed, uint32_t active, bool thisSteam) {
        if (!thisSteam || !active || allowed != 76561197960265728ULL + active) {
            launchedAccount = 0;
            return false;
        }
        return launchedAccount != active;
    }
    void MarkLaunched(uint32_t active) { launchedAccount = active; }
};

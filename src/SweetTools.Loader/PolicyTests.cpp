#include "LaunchPolicy.h"
#include <cassert>

int main() {
    constexpr uint32_t account = 12345;
    constexpr uint64_t allowed = 76561197960265728ULL + account;
    LaunchPolicy policy;
    assert(!policy.ShouldLaunch(0, account, true));
    assert(!policy.ShouldLaunch(allowed, 0, true));
    assert(!policy.ShouldLaunch(allowed, account + 1, true));
    assert(!policy.ShouldLaunch(allowed, account, false));
    assert(policy.ShouldLaunch(allowed, account, true));
    assert(policy.ShouldLaunch(allowed, account, true)); // failed launch can retry on next event
    policy.MarkLaunched(account);
    assert(!policy.ShouldLaunch(allowed, account, true));
    assert(!policy.ShouldLaunch(allowed, 0, true));
    assert(policy.ShouldLaunch(allowed, account, true));
    policy.MarkLaunched(account);
    assert(!policy.ShouldLaunch(allowed, account + 1, true));
    assert(policy.ShouldLaunch(allowed, account, true));
}

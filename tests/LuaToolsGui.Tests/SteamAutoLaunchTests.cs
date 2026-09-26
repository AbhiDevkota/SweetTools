using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamAutoLaunchTests
{
    [Theory]
    [InlineData(null, "76561198000000001", false)]
    [InlineData("", "76561198000000001", false)]
    [InlineData("76561198000000001", null, false)]
    [InlineData("76561198000000001", "76561198000000002", false)]
    [InlineData(" 76561198000000001 ", "76561198000000001", true)]
    public void RequiresMatchingActiveAccount(string? allowed, string? active, bool expected)
    {
        Assert.Equal(expected, SteamAutoLaunchService.MatchesAccount(allowed, active));
    }

    [Fact]
    public void LaunchesOnceAndRearmsAfterLogoutOrSteamRestart()
    {
        var state = new SteamAutoLaunchService.LaunchState();
        Assert.False(state.ShouldLaunch(null));
        Assert.True(state.ShouldLaunch("session1"));
        // A failed process launch remains retryable.
        Assert.True(state.ShouldLaunch("session1"));
        state.MarkLaunched("session1");
        Assert.False(state.ShouldLaunch("session1"));
        Assert.False(state.ShouldLaunch(null));
        Assert.True(state.ShouldLaunch("session1"));
        state.MarkLaunched("session1");
        Assert.True(state.ShouldLaunch("session2"));
    }
}

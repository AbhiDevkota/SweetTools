using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void AppSettings_AllowedSteamId_DefaultsToEmpty()
    {
        var appSettings = new AppSettings();
        Assert.Equal("", appSettings.GetString("AllowedSteamId", ""));
        Assert.Null(appSettings.AllowedSteamId);
    }

    [Fact]
    public void AppSettings_AllowedSteamId_CanBeSetAndRetrieved()
    {
        var appSettings = new AppSettings();
        appSettings.SetString("AllowedSteamId", "76561198000000001");
        Assert.Equal("76561198000000001", appSettings.AllowedSteamId);
        Assert.Equal("76561198000000001", appSettings.GetString("AllowedSteamId", ""));

        appSettings.SetString("AllowedSteamId", "");
        Assert.Null(appSettings.AllowedSteamId);
        Assert.Equal("", appSettings.GetString("AllowedSteamId", ""));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void AllowedSteamId_WhenEmptyOrWhitespace_RepresentsNoAllowedAccount(string? input, bool expectedAllowed)
    {
        bool hasConfiguredAccount = !string.IsNullOrWhiteSpace(input);
        Assert.Equal(expectedAllowed, hasConfiguredAccount);
    }
}

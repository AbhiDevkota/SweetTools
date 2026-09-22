using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class AccountGuardServiceTests
{
    private const string AllowedId = "76561198000000001";
    private const string OtherId = "76561198000000002";

    [Fact]
    public void IsAllowed_ReturnsFalse_WhenAllowedSteamIdNotConfigured()
    {
        var settings = SettingsService.CreateInMemory();
        settings.AllowedSteamId = "";

        var guard = new AccountGuardService(settings, null, null, () => AllowedId);

        Assert.False(guard.IsAllowed());
        var (status, _, _) = guard.CheckStatus();
        Assert.Equal(AccountGuardStatus.NoAccountConfigured, status);
    }

    [Fact]
    public void IsAllowed_ReturnsFalse_WhenNoActiveSteamAccountDetected()
    {
        var settings = SettingsService.CreateInMemory();
        settings.AllowedSteamId = AllowedId;

        var guard = new AccountGuardService(settings, null, null, () => null);

        Assert.False(guard.IsAllowed());
        var (status, current, allowed) = guard.CheckStatus();
        Assert.Equal(AccountGuardStatus.NoActiveSteamAccount, status);
        Assert.Null(current);
        Assert.Equal(AllowedId, allowed);
    }

    [Fact]
    public void IsAllowed_ReturnsFalse_WhenAccountsMismatch()
    {
        var settings = SettingsService.CreateInMemory();
        settings.AllowedSteamId = AllowedId;

        var guard = new AccountGuardService(settings, null, null, () => OtherId);

        Assert.False(guard.IsAllowed());
        var (status, current, allowed) = guard.CheckStatus();
        Assert.Equal(AccountGuardStatus.AccountMismatch, status);
        Assert.Equal(OtherId, current);
        Assert.Equal(AllowedId, allowed);
    }

    [Fact]
    public void IsAllowed_ReturnsTrue_WhenAccountsMatch_CaseInsensitiveAndTrimmed()
    {
        var settings = SettingsService.CreateInMemory();
        settings.AllowedSteamId = "  " + AllowedId + "  ";

        var guard = new AccountGuardService(settings, null, null, () => AllowedId);

        Assert.True(guard.IsAllowed());
        var (status, current, allowed) = guard.CheckStatus();
        Assert.Equal(AccountGuardStatus.Allowed, status);
        Assert.Equal(AllowedId, current);
        Assert.Equal(AllowedId, allowed);
    }

    [Fact]
    public void EnsureAllowed_ReturnsTrue_WhenAllowed()
    {
        var settings = SettingsService.CreateInMemory();
        settings.AllowedSteamId = AllowedId;

        var guard = new AccountGuardService(settings, null, null, () => AllowedId);

        bool allowed = guard.EnsureAllowed("Fetching games", suppressPopup: true);
        Assert.True(allowed);
    }

    [Fact]
    public void EnsureAllowed_ReturnsFalse_WhenMismatch()
    {
        var settings = SettingsService.CreateInMemory();
        settings.AllowedSteamId = AllowedId;

        var guard = new AccountGuardService(settings, null, null, () => OtherId);

        bool allowed = guard.EnsureAllowed("Fetching games", suppressPopup: true);
        Assert.False(allowed);
    }

    [Fact]
    public void EnsureAllowed_ReturnsFalse_WhenUnconfigured()
    {
        var settings = SettingsService.CreateInMemory();
        settings.AllowedSteamId = "";

        var guard = new AccountGuardService(settings, null, null, () => AllowedId);

        bool allowed = guard.EnsureAllowed("Adding games", suppressPopup: true);
        Assert.False(allowed);
    }
}

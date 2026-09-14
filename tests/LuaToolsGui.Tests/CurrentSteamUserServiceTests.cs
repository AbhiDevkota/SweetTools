using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class CurrentSteamUserServiceTests
{
    private const string SampleLoginUsersVdf = """"
"users"
{
	"76561198000000001"
	{
		"AccountName"		"user_one"
		"PersonaName"		"User One"
		"RememberPassword"		"1"
		"MostRecent"		"0"
		"Timestamp"		"1700000000"
	}
	"76561198000000002"
	{
		"AccountName"		"user_two"
		"PersonaName"		"User Two"
		"RememberPassword"		"1"
		"MostRecent"		"1"
		"Timestamp"		"1710000000"
	}
	"76561198000000003"
	{
		"AccountName"		"user_three"
		"PersonaName"		"User Three"
		"RememberPassword"		"1"
		"MostRecent"		"0"
		"Timestamp"		"1705000000"
	}
}
"""";

    [Fact]
    public void ParseCurrentSteamId_FindsUserWithMostRecentOne()
    {
        string? current = CurrentSteamUserService.ParseCurrentSteamId(SampleLoginUsersVdf);
        Assert.Equal("76561198000000002", current);
    }

    [Fact]
    public void ParseCurrentSteamId_FallsBackToAutoLogin_WhenMostRecentMissing()
    {
        const string vdfWithoutMostRecent = """"
"users"
{
	"76561199068614543"
	{
		"AccountName"		"ytsgamerpro"
		"AutoLogin"		"1"
		"Timestamp"		"1789402126"
	}
	"76561199601596436"
	{
		"AccountName"		"4k20p"
		"AutoLogin"		"0"
		"Timestamp"		"1789318351"
	}
}
"""";
        string? current = CurrentSteamUserService.ParseCurrentSteamId(vdfWithoutMostRecent);
        Assert.Equal("76561199068614543", current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a valid vdf")]
    public void ParseCurrentSteamId_ReturnsNullForInvalidInput(string input)
    {
        string? current = CurrentSteamUserService.ParseCurrentSteamId(input);
        Assert.Null(current);
    }

    [Fact]
    public void ParseCurrentSteamId_FallsBackToHighestTimestamp_WhenNoMostRecentOrAutoLogin()
    {
        const string vdf = """"
"users"
{
	"76561198000000010"
	{
		"AccountName"		"older"
		"Timestamp"		"1700000000"
	}
	"76561198000000020"
	{
		"AccountName"		"newer"
		"Timestamp"		"1750000000"
	}
}
"""";
        string? current = CurrentSteamUserService.ParseCurrentSteamId(vdf);
        Assert.Equal("76561198000000020", current);
    }
}

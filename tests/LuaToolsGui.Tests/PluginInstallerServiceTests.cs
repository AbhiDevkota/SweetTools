using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class PluginInstallerServiceTests
{
    [Fact]
    public void VerifiedWinmmSha256_IsPinnedToExactTargetHash()
    {
        const string expectedSha = "dc1594774d3003f7c82fbcdaac4cc9bbc81d7ee2ab82dd5f47c4f04cc3bd8236";
        Assert.Equal(expectedSha, PluginInstallerService.VerifiedWinmmSha256);
    }

    [Fact]
    public void VerifiedWinmmDownloadUrl_PointsToVerifiedLtspRelease()
    {
        const string expectedUrl = "https://github.com/madoiscool/LTSP/releases/download/v2.2/winmm.dll";
        Assert.Equal(expectedUrl, PluginInstallerService.VerifiedWinmmDownloadUrl);
    }

    [Fact]
    public void Slots_IncludesWinmmWithVerifiedShaAndFallbackUrl()
    {
        var winmmSlot = Assert.Single(PluginInstallerService.Slots, s => s.DllAsset == "winmm.dll");
        Assert.Equal("winmm_real.dll", winmmSlot.RealName);
        Assert.Equal("winmm.dll", winmmSlot.SystemSourceName);
        Assert.Equal(PluginInstallerService.VerifiedWinmmSha256, winmmSlot.VerifiedSha256);
        Assert.Equal(PluginInstallerService.VerifiedWinmmDownloadUrl, winmmSlot.FallbackDownloadUrl);
    }

    [Fact]
    public void AssetHash_ParseDigest_CorrectlyParsesSha256PrefixedString()
    {
        const string prefixed = "sha256:dc1594774d3003f7c82fbcdaac4cc9bbc81d7ee2ab82dd5f47c4f04cc3bd8236";
        string? parsed = AssetHash.ParseDigest(prefixed);
        Assert.Equal(PluginInstallerService.VerifiedWinmmSha256, parsed);
    }
}

using System.IO;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class PluginInstallerServiceTests
{
    [Fact]
    public void BundledLoaderHashRejectsModifiedPayload()
    {
        string path = Path.GetTempFileName();
        try
        {
            SteamLauncherService.Extract(path);
            Assert.Equal(AssetHash.OfFile(path), PluginInstallerService.VerifiedWinmmSha256);
            Assert.True(SteamLauncherService.IsBundledLoader(path));
            File.AppendAllText(path, "tamper");
            Assert.False(SteamLauncherService.IsBundledLoader(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoaderHasNoUpstreamFallback()
    {
        Assert.Null(Assert.Single(PluginInstallerService.Slots).FallbackDownloadUrl);
    }

    [Fact]
    public void Slots_IncludesBundledWinmmWithVerifiedHash()
    {
        var winmmSlot = Assert.Single(PluginInstallerService.Slots, s => s.DllAsset == "winmm.dll");
        Assert.Equal("winmm_real.dll", winmmSlot.RealName);
        Assert.Equal("winmm.dll", winmmSlot.SystemSourceName);
        Assert.Equal(PluginInstallerService.VerifiedWinmmSha256, winmmSlot.VerifiedSha256);
        Assert.Null(winmmSlot.FallbackDownloadUrl);
    }

    [Fact]
    public void AssetHash_ParseDigest_CorrectlyParsesSha256PrefixedString()
    {
        const string prefixed = "sha256:dc1594774d3003f7c82fbcdaac4cc9bbc81d7ee2ab82dd5f47c4f04cc3bd8236";
        string? parsed = AssetHash.ParseDigest(prefixed);
        Assert.Equal(prefixed[7..], parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("76561197960265728")]
    [InlineData("76561202255233024")]
    [InlineData("76561198000000001\nother")]
    public void InvalidAccountDisablesNativeLaunch(string? account) =>
        Assert.Equal("", SteamLauncherService.BuildConfiguration(@"C:\Sweet Tools\LuaTools.exe", @"C:\Steam", account));

    [Fact]
    public void ConfigurationPreservesPathsWithSpacesAndNormalizesAccount()
    {
        Assert.Equal("C:\\Sweet Tools\\LuaTools.exe\nC:\\Steam\\steam.exe\n76561198000000001",
            SteamLauncherService.BuildConfiguration(@"C:\Sweet Tools\LuaTools.exe", @"C:\Steam", " 76561198000000001 "));
        Assert.Equal("", SteamLauncherService.BuildConfiguration("C:\\bad\npath", @"C:\Steam", "76561198000000001"));
    }

    [Theory]
    [InlineData(0x8664, true)]
    [InlineData(0x014c, false)]
    [InlineData(0xaa64, false)]
    public void LoaderRequiresX64Steam(ushort machine, bool supported)
    {
        string path = Path.GetTempFileName();
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write((ushort)0x5a4d);
                writer.BaseStream.Position = 0x3c;
                writer.Write(0x80);
                writer.BaseStream.Position = 0x80;
                writer.Write(0x00004550);
                writer.Write(machine);
            }
            Assert.Equal(supported, SteamLauncherService.SupportsSteamExecutable(path));
            File.WriteAllText(path, "not an executable");
            Assert.False(SteamLauncherService.SupportsSteamExecutable(path));
        }
        finally { File.Delete(path); }
    }
}

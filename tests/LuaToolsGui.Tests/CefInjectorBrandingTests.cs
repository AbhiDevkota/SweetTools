using System.IO;
using System.Linq;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class CefInjectorBrandingTests
{
    [Fact]
    public void SweetToolsIconPngDataUrl_IsValidPngDataUrl()
    {
        Assert.StartsWith("data:image/png;base64,", HttpServerService.SweetToolsIconPngDataUrl);
        string raw = HttpServerService.SweetToolsIconPngBase64;
        Assert.True(raw.Length % 4 == 0, $"Length is {raw.Length}, mod 4 is {raw.Length % 4}");
        
        byte[] bytes = System.Convert.FromBase64String(raw);
        Assert.NotEmpty(bytes);
        // Standard PNG magic bytes: 0x89, 0x50, 0x4E, 0x47 ('\x89PNG')
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    [Fact]
    public void BrandTransformScript_ReplacesAddViaLuaToolsWithAddViaSweetTools()
    {
        const string input = "button.title = \"Add via LuaTools\"; btnSpan.innerText = 'Add via LuaTools'; remove.title = \"Remove via LuaTools\";";
        string output = CefInjectorService.BrandTransformScript(input);

        Assert.Contains("\"Add via SweetTools\"", output);
        Assert.Contains("'Add via SweetTools'", output);
        Assert.Contains("\"Remove via SweetTools\"", output);
        Assert.DoesNotContain("Add via LuaTools", output);
        Assert.DoesNotContain("Remove via LuaTools", output);
    }

    [Fact]
    public void BrandTransformScript_ReplacesLuaToolsHeaderAndMenuTitlesWithSteamTools()
    {
        const string input = "header.innerText = \"LuaTools Settings\"; aria-label=\"LuaTools\"; alt=\"LuaTools\"; title: \"LuaTools • \";";
        string output = CefInjectorService.BrandTransformScript(input);

        Assert.Contains("\"Steam Tools Settings\"", output);
        Assert.Contains("aria-label=\"Steam Tools\"", output);
        Assert.Contains("alt=\"Steam Tools\"", output);
        Assert.Contains("Steam Tools •", output);
        Assert.DoesNotContain("LuaTools Settings", output);
    }

    [Fact]
    public void BrandTransformScript_ReplacesOldFallbackIconDataUrl()
    {
        const string oldIconUrl = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAACdklEQVRYR+2Wv0t";
        string input = $"var fallback = '{oldIconUrl}';";
        string output = CefInjectorService.BrandTransformScript(input);

        Assert.Contains(HttpServerService.SweetToolsIconPngDataUrl, output);
        Assert.DoesNotContain(oldIconUrl, output);
    }

    [Fact]
    public void ApplyBrandingToFrontend_ExecutesWithoutException()
    {
        var ex = Record.Exception(() => PluginInstallerService.ApplyBrandingToFrontend());
        Assert.Null(ex);
    }
}

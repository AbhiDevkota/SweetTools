using System.IO;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public class SteamAppListCacheTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _binPath;
    private readonly string _jsonPath;

    public SteamAppListCacheTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SteamAppListCacheTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _binPath = Path.Combine(_testDir, "steam-applist.bin");
        _jsonPath = Path.Combine(_testDir, "steam-applist.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetName_ReturnsCorrectNames_FromBinaryIndex()
    {
        var entries = new List<KeyValuePair<long, string>>
        {
            new(730, "Counter-Strike 2"),
            new(570, "Dota 2"),
            new(10, "Counter-Strike"),
            new(1245620, "ELDEN RING")
        };

        bool wrote = SteamAppListCache.WriteBinaryIndex(entries, _binPath);
        Assert.True(wrote);

        using var cache = new SteamAppListCache(_binPath, _jsonPath);
        await cache.EnsureLoadedAsync();

        Assert.Equal("Counter-Strike 2", cache.GetName(730));
        Assert.Equal("Dota 2", cache.GetName(570));
        Assert.Equal("Counter-Strike", cache.GetName(10));
        Assert.Equal("ELDEN RING", cache.GetName(1245620));
        Assert.Null(cache.GetName(99999999));
    }

    [Fact]
    public async Task EnsureLoadedAsync_MigratesLegacyJson()
    {
        string json = """
        {
            "730": "Counter-Strike 2",
            "1086940": "Baldur's Gate 3",
            "400": "Portal"
        }
        """;
        await File.WriteAllTextAsync(_jsonPath, json);

        using var cache = new SteamAppListCache(_binPath, _jsonPath);
        await cache.EnsureLoadedAsync();

        Assert.True(File.Exists(_binPath));
        Assert.Equal("Counter-Strike 2", cache.GetName(730));
        Assert.Equal("Baldur's Gate 3", cache.GetName(1086940));
        Assert.Equal("Portal", cache.GetName(400));
        Assert.Null(cache.GetName(999));
    }

    [Fact]
    public async Task GetName_ConcurrentLookups_ThreadSafe()
    {
        var entries = new List<KeyValuePair<long, string>>();
        for (int i = 1; i <= 1000; i++)
        {
            entries.Add(new KeyValuePair<long, string>(i * 10, $"Game {i * 10}"));
        }

        SteamAppListCache.WriteBinaryIndex(entries, _binPath);

        using var cache = new SteamAppListCache(_binPath, _jsonPath);
        await cache.EnsureLoadedAsync();

        Parallel.For(1, 1000, i =>
        {
            long id = i * 10;
            string? name = cache.GetName(id);
            Assert.Equal($"Game {id}", name);
        });
    }
}

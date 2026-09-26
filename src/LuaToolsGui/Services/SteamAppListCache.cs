using System.Collections.Concurrent;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>
/// Bulk appid → name lookup. Primary source is the Morrenus app list (a single static JSON,
/// ~341k entries incl. delisted apps, no rate limit); falls back to Steam's GetAppList.
/// Indexed on disk as a compact binary format and memory-mapped for zero-heap, sub-millisecond
/// lookups without keeping hundreds of thousands of strings in RAM.
/// </summary>
public class SteamAppListCache : IDisposable
{
    private const string MorrenusUrl = "https://applist.morrenus.xyz/";
    private const string SteamUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";

    private const uint Magic = 0x53544150; // "STAP"
    private const int Version = 1;
    private const int IndexEntrySize = 14; // long (8) + int (4) + ushort (2)
    private const int HeaderSize = 28;    // Magic (4) + Version (4) + Count (4) + IndexOffset (8) + StringTableOffset (8)

    private static readonly string DefaultDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui");
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly string _binPath;
    private readonly string _legacyJsonPath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // In-memory cache for resolved games only (few dozen to few hundred items at most, not 341k)
    private readonly ConcurrentDictionary<long, string?> _resolvedNames = new();
    private readonly object _gate = new();

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private int _entryCount;
    private long _indexOffset;
    private long _stringTableOffset;
    private Task? _loadTask;
    private bool _disposed;

    public SteamAppListCache() : this(Path.Combine(DefaultDir, "steam-applist.bin"), Path.Combine(DefaultDir, "steam-applist.json"))
    {
    }

    internal SteamAppListCache(string binPath, string legacyJsonPath)
    {
        _binPath = binPath;
        _legacyJsonPath = legacyJsonPath;
    }

    public string? GetName(long appid)
    {
        if (_resolvedNames.TryGetValue(appid, out var cached))
            return cached;

        lock (_gate)
        {
            if (_disposed || _accessor is null || _entryCount == 0)
                return null;

            int low = 0;
            int high = _entryCount - 1;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                long entryPos = _indexOffset + ((long)mid * IndexEntrySize);
                long midId = _accessor.ReadInt64(entryPos);

                if (midId == appid)
                {
                    int strOffset = _accessor.ReadInt32(entryPos + 8);
                    ushort strLen = _accessor.ReadUInt16(entryPos + 12);

                    byte[] buffer = new byte[strLen];
                    _accessor.ReadArray(_stringTableOffset + strOffset, buffer, 0, strLen);
                    string name = Encoding.UTF8.GetString(buffer);

                    _resolvedNames[appid] = name;
                    return name;
                }

                if (midId < appid)
                    low = mid + 1;
                else
                    high = mid - 1;
            }
        }

        _resolvedNames[appid] = null;
        return null;
    }

    /// <summary>Ensure the name list is loaded (from disk, migrated from json, or downloaded once). Safe to call repeatedly.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        // 1. Fresh binary cache already exists → open memory-mapped view
        if (File.Exists(_binPath))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(_binPath) < MaxAge)
                {
                    if (TryOpenBinaryIndex(_binPath))
                        return;
                }
            }
            catch { /* fall through to rebuild or download */ }
        }

        // 2. Legacy JSON cache exists → migrate to binary once
        if (File.Exists(_legacyJsonPath))
        {
            try
            {
                if (await TryMigrateJsonToBinaryAsync(_legacyJsonPath, _binPath))
                {
                    if (TryOpenBinaryIndex(_binPath))
                    {
                        try { File.Delete(_legacyJsonPath); } catch { /* best effort */ }
                        return;
                    }
                }
            }
            catch { /* fall through to download */ }
        }

        // 3. Download fresh: Morrenus first (reliable, includes delisted), then Steam as fallback
        if (await TryDownloadAndBuildBinaryAsync(MorrenusUrl, flatArray: true, _binPath) ||
            await TryDownloadAndBuildBinaryAsync(SteamUrl, flatArray: false, _binPath))
        {
            if (TryOpenBinaryIndex(_binPath))
            {
                try { if (File.Exists(_legacyJsonPath)) File.Delete(_legacyJsonPath); } catch { }
                return;
            }
        }

        // 4. Fallback: try opening any existing binary cache even if expired
        if (File.Exists(_binPath) && TryOpenBinaryIndex(_binPath))
            return;
    }

    private bool TryOpenBinaryIndex(string path)
    {
        lock (_gate)
        {
            CloseIndex();
            try
            {
                var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _mmf = MemoryMappedFile.CreateFromFile(fileStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                uint magic = _accessor.ReadUInt32(0);
                int version = _accessor.ReadInt32(4);
                if (magic != Magic || version != Version)
                {
                    CloseIndex();
                    return false;
                }

                _entryCount = _accessor.ReadInt32(8);
                _indexOffset = _accessor.ReadInt64(12);
                _stringTableOffset = _accessor.ReadInt64(20);

                return _entryCount > 0;
            }
            catch
            {
                CloseIndex();
                return false;
            }
        }
    }

    private void CloseIndex()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        _entryCount = 0;
    }

    private static async Task<bool> TryMigrateJsonToBinaryAsync(string jsonPath, string binPath)
    {
        try
        {
            await using var stream = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var entries = new List<KeyValuePair<long, string>>();
            foreach (var prop in root.EnumerateObject())
            {
                if (long.TryParse(prop.Name, out long id))
                {
                    string? name = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        entries.Add(new KeyValuePair<long, string>(id, name));
                }
            }

            if (entries.Count == 0) return false;
            return WriteBinaryIndex(entries, binPath);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryDownloadAndBuildBinaryAsync(string url, bool flatArray, string binPath)
    {
        try
        {
            await using var stream = await _http.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            var apps = flatArray
                ? doc.RootElement
                : doc.RootElement.GetProperty("applist").GetProperty("apps");

            var entries = new List<KeyValuePair<long, string>>();
            foreach (var app in apps.EnumerateArray())
            {
                if (app.TryGetProperty("appid", out var idProp) && idProp.TryGetInt64(out long id))
                {
                    string? name = app.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(name))
                        entries.Add(new KeyValuePair<long, string>(id, name));
                }
            }

            if (entries.Count == 0) return false;
            return WriteBinaryIndex(entries, binPath);
        }
        catch
        {
            return false;
        }
    }

    internal static bool WriteBinaryIndex(List<KeyValuePair<long, string>> entries, string targetFile)
    {
        try
        {
            // Sort by AppId ascending and deduplicate
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));

            var deduplicated = new List<KeyValuePair<long, string>>(entries.Count);
            long lastId = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Key == lastId) continue;
                lastId = entries[i].Key;
                deduplicated.Add(entries[i]);
            }

            string? dir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmpPath = targetFile + ".tmp";
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var strMs = new MemoryStream())
            using (var bw = new BinaryWriter(fs))
            {
                long indexStart = HeaderSize;
                long stringTableOffset = indexStart + ((long)deduplicated.Count * IndexEntrySize);

                bw.Write(Magic);
                bw.Write(Version);
                bw.Write(deduplicated.Count);
                bw.Write(indexStart);
                bw.Write(stringTableOffset);

                foreach (var kvp in deduplicated)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(kvp.Value);
                    int offset = (int)strMs.Position;
                    ushort length = (ushort)Math.Min(utf8.Length, ushort.MaxValue);
                    strMs.Write(utf8, 0, length);

                    bw.Write(kvp.Key);
                    bw.Write(offset);
                    bw.Write(length);
                }

                strMs.Position = 0;
                strMs.CopyTo(fs);
            }

            File.Move(tmpPath, targetFile, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseIndex();
            _http.Dispose();
        }
    }
}

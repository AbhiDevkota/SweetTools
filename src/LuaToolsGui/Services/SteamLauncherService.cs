using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>Configuration and bundled payload for the native, event-driven Steam launcher.</summary>
internal static class SteamLauncherService
{
    internal const string LaunchArgument = "--steam-account-launch";
    private const string KeyPath = @"Software\SweetTools\SteamLauncher";
    private const string ResourceName = "SweetTools.Loader.winmm.dll";
    internal static string PayloadHash { get; } = ComputeHash();

    private static Stream OpenPayload() => typeof(SteamLauncherService).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("The SweetTools native launcher was not bundled.");

    private static string ComputeHash()
    {
        using var payload = OpenPayload();
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    internal static void Extract(string path)
    {
        using var payload = OpenPayload();
        using var file = File.Create(path);
        payload.CopyTo(file);
    }

    internal static bool IsBundledLoader(string path) => File.Exists(path) &&
        string.Equals(AssetHash.OfFile(path), PayloadHash, StringComparison.OrdinalIgnoreCase);

    internal static bool SupportsSteamExecutable(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (!Environment.Is64BitProcess || reader.ReadUInt16() != 0x5a4d) return false;
            reader.BaseStream.Position = 0x3c;
            int header = reader.ReadInt32();
            if (header < 0x40 || header > reader.BaseStream.Length - 6) return false;
            reader.BaseStream.Position = header;
            return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x8664;
        }
        catch { return false; }
    }

    internal static string BuildConfiguration(string? exe, string? steamPath, string? allowed)
    {
        if (string.IsNullOrWhiteSpace(exe) || string.IsNullOrWhiteSpace(steamPath) ||
            exe.IndexOfAny(['\r', '\n', '"']) >= 0 || steamPath.IndexOfAny(['\r', '\n', '"']) >= 0 ||
            !ulong.TryParse(allowed?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ||
            id <= 76561197960265728UL || id > 76561197960265728UL + uint.MaxValue) return "";
        return $"{Path.GetFullPath(exe)}\n{Path.Combine(Path.GetFullPath(steamPath), "steam.exe")}\n{id}";
    }

    internal static void Configure(SettingsService settings)
    {
        try
        {
            string value = BuildConfiguration(Environment.ProcessPath, new SteamService(settings).EffectivePath, settings.AllowedSteamId);
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            // One atomic value prevents the DLL from combining old account and new executable paths.
            if (!string.Equals(key.GetValue("Configuration") as string, value, StringComparison.Ordinal))
                key.SetValue("Configuration", value);
        }
        catch (Exception ex) { Trace.TraceError("Steam launcher configuration failed: {0}", ex); }
    }

    internal static bool IsAllowedLaunch(SettingsService settings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("pid") is not int pid || pid <= 0) return false;
            using var process = Process.GetProcessById(pid);
            string? steamPath = new SteamService(settings).EffectivePath;
            if (steamPath is null || process.HasExited || !string.Equals(process.MainModule?.FileName,
                    Path.Combine(steamPath, "steam.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            string? current = CurrentSteamUserService.GetActiveProcessSteamId(steamPath);
            return !string.IsNullOrWhiteSpace(current) && current == settings.AllowedSteamId.Trim();
        }
        catch { return false; }
    }
}

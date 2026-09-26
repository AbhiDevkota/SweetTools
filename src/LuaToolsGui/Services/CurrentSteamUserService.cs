using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>
/// Detects the currently logged-in Steam account by reading Steam's ActiveProcess registry and config/loginusers.vdf.
/// </summary>
public class CurrentSteamUserService : IDisposable
{
    private readonly SteamService _steam;
    private readonly ILogger<CurrentSteamUserService>? _log;

    private static readonly Regex UserBlockRegex = new(
        @"""(\d{16,19})""\s*\{([^}]*)\}",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MostRecentRegex = new(
        @"""MostRecent""\s*""1""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AutoLoginRegex = new(
        @"""AutoLogin""\s*""1""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimestampRegex = new(
        @"""Timestamp""\s*""(\d+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AccountNameRegex = new(
        @"""AccountName""\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Aliases to support camelCase identifiers in IDE editors and refactorings
    private static Regex userBlockRegex => UserBlockRegex;
    private static Regex mostRecentRegex => MostRecentRegex;
    private static Regex autoLoginRegex => AutoLoginRegex;
    private static Regex timestampRegex => TimestampRegex;
    private static Regex accountNameRegex => AccountNameRegex;

    private readonly object _accountLock = new();
    private string? _lastKnownSteamId;
    private DateTime _lastVdfWriteTime = DateTime.MinValue;
    private string? _lastRegAutoLoginUser;
    private string? _cachedVdfSteamId;
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// Raised whenever Steam's config/loginusers.vdf changes on disk and a new active account is detected.
    /// </summary>
    public event Action<string?>? ActiveAccountChanged;

    /// <summary>
    /// The last detected active Steam account ID.
    /// </summary>
    public string? LastKnownSteamId
    {
        get
        {
            lock (_accountLock) return _lastKnownSteamId;
        }
    }

    private readonly System.Timers.Timer _pollTimer;

    /// <summary>
    /// Initializes a new instance of <see cref="CurrentSteamUserService"/>.
    /// </summary>
    /// <param name="steam">The SteamService used to resolve Steam's install directory.</param>
    /// <param name="log">Optional logger for diagnostic messages.</param>
    public CurrentSteamUserService(SteamService steam, ILogger<CurrentSteamUserService>? log = null)
    {
        _steam = steam;
        _log = log;
        _lastKnownSteamId = GetCurrentSteamId();
        InitWatcher();

        // Active polling monitor ensures account changes are detected even if filesystem events lag
        _pollTimer = new System.Timers.Timer(2500) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => CheckForAccountChange();
        _pollTimer.Start();
    }

    private void CheckForAccountChange()
    {
        string? newId = GetCurrentSteamId();
        if (string.IsNullOrWhiteSpace(newId))
            return;

        bool changed = false;
        lock (_accountLock)
        {
            if (!string.Equals(newId, _lastKnownSteamId, StringComparison.OrdinalIgnoreCase))
            {
                _lastKnownSteamId = newId;
                changed = true;
            }
        }

        if (changed)
        {
            _log?.LogInformation("Active Steam account changed to {SteamId}", newId);
            ActiveAccountChanged?.Invoke(newId);
        }
    }

    private void InitWatcher()
    {
        try
        {
            string? path = LoginUsersPath;
            if (path is null) return;
            string? dir = Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir)
            {
                Filter = "*loginusers*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            var debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
            debounceTimer.Elapsed += (_, _) => CheckForAccountChange();

            void OnFileChanged(object sender, FileSystemEventArgs e)
            {
                debounceTimer.Stop();
                debounceTimer.Start();
            }

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.Error += (_, _) =>
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.EnableRaisingEvents = true;
                }
                catch { }
            };
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to initialize loginusers.vdf watcher");
        }
    }

    /// <summary>
    /// Full path to Steam's config/loginusers.vdf, or null if Steam cannot be located.
    /// </summary>
    public string? LoginUsersPath =>
        _steam.EffectivePath is { } path ? Path.Combine(path, "config", "loginusers.vdf") : null;

    /// <summary>
    /// Gets the SteamID64 of the currently logged-in user, or null if unavailable.
    /// </summary>
    public string? CurrentSteamId => GetCurrentSteamId();

    /// <summary>
    /// Gets the SteamID64 of the actively running Steam user from the registry, or null if Steam is not running or no active user.
    /// If <paramref name="expectedSteamPath"/> is specified and exists, only returns the ID if the running Steam process matches that folder.
    /// </summary>
    public static string? GetActiveProcessSteamId(string? expectedSteamPath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(expectedSteamPath) && Directory.Exists(expectedSteamPath))
            {
                using var steamKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (steamKey?.GetValue("SteamPath") is string regPath && !string.IsNullOrWhiteSpace(regPath))
                {
                    string normExpected = Path.GetFullPath(expectedSteamPath.Trim().Replace('/', '\\')).TrimEnd('\\', '/');
                    string normReg = Path.GetFullPath(regPath.Trim().Replace('/', '\\')).TrimEnd('\\', '/');
                    if (!string.Equals(normExpected, normReg, StringComparison.OrdinalIgnoreCase))
                        return null; // The running Steam process is from a different folder
                }
            }

            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam\ActiveProcess");
            object? val = key?.GetValue("ActiveUser");
            if (val is not null && long.TryParse(val.ToString(), out long activeUser32) && activeUser32 > 0)
            {
                ulong steamId64 = 76561197960265728UL + (ulong)(uint)activeUser32;
                return steamId64.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Gets the AutoLoginUser account name saved in Steam's registry, or null if unreadable.
    /// </summary>
    public static string? GetRegistryAutoLoginUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            return key?.GetValue("AutoLoginUser") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads Steam's config/loginusers.vdf and returns the SteamID64 of the current user.
    /// Returns null if the file is missing, unreadable, or no current user can be determined.
    /// </summary>
    /// <returns>The SteamID64 string, or null on failure.</returns>
    public string? GetCurrentSteamId()
    {
        // 1. If Steam is actively running for this Steam directory, ActiveProcess is 100% authoritative and instantaneous
        string? activeProcessId = GetActiveProcessSteamId(_steam.EffectivePath);
        if (!string.IsNullOrWhiteSpace(activeProcessId))
            return activeProcessId;

        // 2. Fall back to parsing loginusers.vdf (when Steam is closed or transitioning)
        string? path = LoginUsersPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            try
            {
                using var steamKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (steamKey?.GetValue("SteamPath") is string regPath && !string.IsNullOrWhiteSpace(regPath))
                {
                    string candidate = Path.Combine(regPath.Trim().Replace('/', '\\'), "config", "loginusers.vdf");
                    if (File.Exists(candidate))
                        path = candidate;
                }
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _log?.LogWarning("Steam loginusers.vdf not found at {Path}", path ?? "null");
            return null;
        }

        string? regAutoLoginUser = GetRegistryAutoLoginUser();

        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        if (lastWrite == _lastVdfWriteTime &&
            string.Equals(regAutoLoginUser, _lastRegAutoLoginUser, StringComparison.OrdinalIgnoreCase) &&
            _cachedVdfSteamId is not null)
        {
            return _cachedVdfSteamId;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string content = reader.ReadToEnd();

                string? steamId = ParseCurrentSteamId(content, regAutoLoginUser);
                if (steamId is not null)
                {
                    _lastVdfWriteTime = lastWrite;
                    _lastRegAutoLoginUser = regAutoLoginUser;
                    _cachedVdfSteamId = steamId;
                    return steamId;
                }

                _log?.LogWarning("Failed to extract current Steam account ID from {Path}", path);
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "Attempt {Attempt} reading loginusers.vdf failed", attempt + 1);
            }

            if (attempt < 2)
                Thread.Sleep(100);
        }

        return null;
    }

    /// <summary>
    /// Checks whether the provided SteamID matches the currently logged-in account.
    /// </summary>
    /// <param name="steamId">The SteamID64 to compare against.</param>
    /// <returns>True if the provided ID matches the current account; otherwise, false.</returns>
    public bool IsCurrentUser(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return false;

        string? current = GetCurrentSteamId();
        return string.Equals(current, steamId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the SteamID64 of the active user from the raw text content of loginusers.vdf.
    /// Prefers the account marked with "MostRecent" = "1", falling back to the account matching
    /// <paramref name="preferredAccountName"/> (e.g. from registry AutoLoginUser), the highest timestamp,
    /// "AutoLogin" = "1", or the first account entry if no specific markers are present.
    /// </summary>
    /// <param name="vdfContent">The text content of loginusers.vdf.</param>
    /// <param name="preferredAccountName">Optional account name (e.g. from registry AutoLoginUser) to match against.</param>
    /// <returns>The extracted SteamID64 string, or null if no valid account entry was found.</returns>
    public static string? ParseCurrentSteamId(string vdfContent, string? preferredAccountName = null)
    {
        if (string.IsNullOrWhiteSpace(vdfContent))
            return null;

        try
        {
            var matches = UserBlockRegex.Matches(vdfContent);
            if (matches.Count == 0)
                return null;

            string? mostRecentId = null;
            string? matchedAccountNameId = null;
            string? autoLoginId = null;
            string? highestTimestampId = null;
            long maxTimestamp = -1;
            string? firstId = null;

            foreach (Match match in matches)
            {
                string id = match.Groups[1].Value;
                string block = match.Groups[2].Value;

                firstId ??= id;

                if (MostRecentRegex.IsMatch(block))
                {
                    mostRecentId = id;
                    break; // Primary target matched!
                }

                if (!string.IsNullOrWhiteSpace(preferredAccountName))
                {
                    var accMatch = AccountNameRegex.Match(block);
                    if (accMatch.Success && string.Equals(accMatch.Groups[1].Value, preferredAccountName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        matchedAccountNameId = id;
                    }
                }

                if (AutoLoginRegex.IsMatch(block))
                {
                    autoLoginId = id;
                }

                var tsMatch = TimestampRegex.Match(block);
                if (tsMatch.Success && long.TryParse(tsMatch.Groups[1].Value, out long ts))
                {
                    if (ts > maxTimestamp)
                    {
                        maxTimestamp = ts;
                        highestTimestampId = id;
                    }
                }
            }

            return mostRecentId ?? matchedAccountNameId ?? highestTimestampId ?? autoLoginId ?? firstId;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        _watcher?.Dispose();
    }
}

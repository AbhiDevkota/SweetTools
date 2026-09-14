using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

/// <summary>
/// Detects the currently logged-in Steam account by reading and parsing Steam's config/loginusers.vdf.
/// </summary>
public class CurrentSteamUserService
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

    /// <summary>
    /// Initializes a new instance of <see cref="CurrentSteamUserService"/>.
    /// </summary>
    /// <param name="steam">The SteamService used to resolve Steam's install directory.</param>
    /// <param name="log">Optional logger for diagnostic messages.</param>
    public CurrentSteamUserService(SteamService steam, ILogger<CurrentSteamUserService>? log = null)
    {
        _steam = steam;
        _log = log;
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
    /// Reads Steam's config/loginusers.vdf and returns the SteamID64 of the current user.
    /// Returns null if the file is missing, unreadable, or no current user can be determined.
    /// </summary>
    /// <returns>The SteamID64 string, or null on failure.</returns>
    public string? GetCurrentSteamId()
    {
        try
        {
            string? path = LoginUsersPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _log?.LogWarning("Steam loginusers.vdf not found at {Path}", path ?? "null");
                return null;
            }

            string content = File.ReadAllText(path);
            string? steamId = ParseCurrentSteamId(content);
            if (steamId is null)
            {
                _log?.LogWarning("Failed to extract current Steam account ID from {Path}", path);
            }
            return steamId;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Error reading Steam loginusers.vdf");
            return null;
        }
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
    /// Prefers the account marked with "MostRecent" = "1", falling back to "AutoLogin" = "1",
    /// highest timestamp, or the first account entry if no specific markers are present.
    /// </summary>
    /// <param name="vdfContent">The text content of loginusers.vdf.</param>
    /// <returns>The extracted SteamID64 string, or null if no valid account entry was found.</returns>
    public static string? ParseCurrentSteamId(string vdfContent)
    {
        if (string.IsNullOrWhiteSpace(vdfContent))
            return null;

        try
        {
            var matches = UserBlockRegex.Matches(vdfContent);
            if (matches.Count == 0)
                return null;

            string? mostRecentId = null;
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

            return mostRecentId ?? autoLoginId ?? highestTimestampId ?? firstId;
        }
        catch
        {
            return null;
        }
    }
}

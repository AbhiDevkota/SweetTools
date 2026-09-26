using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>A separate, UI-free watcher survives closing the app and removal of Steam plugins.</summary>
internal static class SteamAutoLaunchService
{
    internal const string WatchArgument = "--watch-steam";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "SweetToolsSteamWatcher";
    private const string StopName = "SweetTools.StopSteamWatcher";

    internal static void Configure(SettingsService settings)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (string.IsNullOrWhiteSpace(settings.AllowedSteamId))
            {
                key.DeleteValue(RunName, false);
                if (EventWaitHandle.TryOpenExisting(StopName, out var stop))
                {
                    using (stop) stop.Set();
                }
                return;
            }

            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            key.SetValue(RunName, $"\"{exe}\" {WatchArgument}");
            using var child = Process.Start(new ProcessStartInfo(exe, WatchArgument)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Trace.TraceError("Could not configure Steam auto-launch: {0}", ex);
        }
    }

    internal static void Run()
    {
        using var single = new Mutex(true, "SweetTools.SteamWatcher", out bool first);
        if (!first) return;
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopName);
        var state = new LaunchState();
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LuaToolsGui", "settings.json");
        do
        {
            try
            {
                // Read fresh each time; an unreadable file must never authorize a launch.
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (settings is null) continue;
                if (string.IsNullOrWhiteSpace(settings.AllowedSteamId)) return;
                string? session = GetAllowedSession(settings);
                if (state.ShouldLaunch(session))
                {
                    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = false
                    });
                    if (child is not null) state.MarkLaunched(session!);
                }
            }
            catch (Exception ex)
            {
                state.ShouldLaunch(null);
                Trace.TraceError("Steam watcher check failed: {0}", ex);
            }
        } while (!stop.WaitOne(TimeSpan.FromSeconds(2)));
    }

    private static string? GetAllowedSession(AppSettings settings)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam\ActiveProcess");
        if (key?.GetValue("pid") is not int pid || pid <= 0) return null;
        using var steam = Process.GetProcessById(pid);
        if (steam.HasExited || !steam.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.IsNullOrWhiteSpace(settings.SteamPathOverride))
        {
            string? actual = Path.GetDirectoryName(steam.MainModule?.FileName);
            if (actual is null || !Path.GetFullPath(actual).TrimEnd('\\').Equals(
                    Path.GetFullPath(settings.SteamPathOverride).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return null;
        }
        string? active = CurrentSteamUserService.GetActiveProcessSteamId(settings.SteamPathOverride);
        return MatchesAccount(settings.AllowedSteamId, active)
            ? $"{pid}:{steam.StartTime.ToUniversalTime().Ticks}:{active}" : null;
    }

    internal static bool MatchesAccount(string? allowed, string? active) =>
        !string.IsNullOrWhiteSpace(allowed) && !string.IsNullOrWhiteSpace(active) &&
        string.Equals(allowed.Trim(), active.Trim(), StringComparison.Ordinal);

    internal sealed class LaunchState
    {
        private string? _launched;
        internal bool ShouldLaunch(string? session)
        {
            if (session is null) _launched = null;
            return session is not null && session != _launched;
        }
        internal void MarkLaunched(string session) => _launched = session;
    }
}

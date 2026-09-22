namespace LuaToolsGui.Services;

/// <summary>
/// Anonymous app-launch analytics service.
/// All telemetry data sending to the server has been removed.
/// </summary>
public class AnalyticsService
{
    /// <summary>No-op: telemetry has been removed.</summary>
    public Task TrackAppLaunchAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}

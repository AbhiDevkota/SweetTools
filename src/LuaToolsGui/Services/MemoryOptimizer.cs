using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LuaToolsGui.Services;

/// <summary>
/// Proactively reclaims unused memory and compacts GC heaps, releasing physical working set
/// memory back to Windows when the application is idle, minimized, or hidden in the system tray.
/// </summary>
public static class MemoryOptimizer
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static readonly object _trimLock = new();
    private static DateTime _lastTrim = DateTime.MinValue;

    /// <summary>
    /// Collects all GC generations, waits for finalizers, compacts large object heaps,
    /// and empties unreferenced pages from the process working set.
    /// Throttled to avoid thrashing if called in quick succession.
    /// </summary>
    public static void TrimMemory(bool force = false)
    {
        lock (_trimLock)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastTrim) < TimeSpan.FromSeconds(3))
                return;
            _lastTrim = now;
        }

        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            if (OperatingSystem.IsWindows())
            {
                using var currentProcess = Process.GetCurrentProcess();
                EmptyWorkingSet(currentProcess.Handle);
            }
        }
        catch
        {
            // Trimming is best-effort and should never throw
        }
    }
}

using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Tentacle.Broker;

/// <summary>
/// Reads the exit code of a local job. The shim execs ffmpeg in place, so the job is
/// Jellyfin's own child process; .NET reaps it and keeps the status in the child's
/// internal ProcessWaitState, which Process.GetProcessById cannot see ("not started
/// by this object"). Capture that state while the process runs, read it after it
/// exits. Best effort over runtime internals (checked by the e2e tests): if the
/// layout ever changes, outcomes fall back to "exited".
/// </summary>
internal static class LocalExitTracker
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type? WaitStateType = typeof(Process).Assembly.GetType("System.Diagnostics.ProcessWaitState");
    private static readonly FieldInfo? ChildStates = WaitStateType?.GetField("s_childProcessWaitStates", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo? ExitedField = WaitStateType?.GetField("_exited", Instance);
    private static readonly FieldInfo? ExitCodeField = WaitStateType?.GetField("_exitCode", Instance);

    /// <summary>
    /// Captures the wait state of a running child.
    /// </summary>
    /// <param name="pid">The child's pid.</param>
    /// <returns>An opaque handle, or null when the pid is not a child or the runtime layout differs.</returns>
    public static object? Capture(int pid)
    {
        if (pid <= 0 || ChildStates?.GetValue(null) is not IDictionary children || ExitedField is null || ExitCodeField is null)
        {
            return null;
        }

        // The runtime locks the dictionary object itself (Dictionary's SyncRoot is itself).
        lock (children.SyncRoot)
        {
            return children.Contains(pid) ? children[pid] : null;
        }
    }

    /// <summary>
    /// Waits for the captured child to be reaped and returns its exit code.
    /// </summary>
    /// <param name="state">The handle from <see cref="Capture"/>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The exit code (128+n after signal n), or null.</returns>
    public static async Task<int?> ExitCodeAsync(object? state, TimeSpan timeout)
    {
        if (state is null || ExitedField is null || ExitCodeField is null)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ExitedField.GetValue(state) is true)
            {
                return ExitCodeField.GetValue(state) as int?;
            }

            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
        }

        return null;
    }
}

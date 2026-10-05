using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Tentacle.Broker;

/// <summary>
/// One job as the dashboard shows it. Mutable while the job runs.
/// </summary>
public sealed class JobRecord
{
    /// <summary>Gets or sets the job id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the job kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the node it ran on ("local" for the server).</summary>
    public string Node { get; set; } = "local";

    /// <summary>Gets or sets where it would have run, in shadow mode.</summary>
    public string? WouldRunOn { get; set; }

    /// <summary>Gets or sets why it was placed where it was.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets when it started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Gets or sets the run time in milliseconds, once ended.</summary>
    public long? DurationMs { get; set; }

    /// <summary>Gets or sets the outcome: running, ok, error, killed, lost, exited.</summary>
    public string Outcome { get; set; } = "running";

    /// <summary>Gets or sets the exit code, when known.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Gets or sets the nice value the job runs with on a tentacle (0 = normal).</summary>
    public int Nice { get; set; }

    /// <summary>Gets or sets the command line, shortened.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user the job serves (playback only).</summary>
    public string? User { get; set; }

    /// <summary>Gets or sets the client app and device, e.g. "Jellyfin Web on Firefox".</summary>
    public string? Client { get; set; }

    /// <summary>Gets or sets what the job works on, e.g. "Andor S01E03: Reckoning".</summary>
    public string? Item { get; set; }

    /// <summary>Gets or sets the library item id.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets Jellyfin's play session id (playback only).</summary>
    public string? PlaySessionId { get; set; }
}

/// <summary>
/// The last jobs, newest first: a bounded ring.
/// </summary>
public sealed class JobHistory
{
    /// <summary>How many jobs are kept.</summary>
    public const int Capacity = 500;

    private readonly LinkedList<JobRecord> _jobs = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Adds a job.
    /// </summary>
    /// <param name="record">The job.</param>
    public void Add(JobRecord record)
    {
        lock (_lock)
        {
            _jobs.AddFirst(record);
            if (_jobs.Count > Capacity)
            {
                _jobs.RemoveLast();
            }
        }
    }

    /// <summary>
    /// The newest jobs.
    /// </summary>
    /// <param name="limit">How many.</param>
    /// <returns>Copies, newest first.</returns>
    public IReadOnlyList<JobRecord> Snapshot(int limit = 100)
    {
        lock (_lock)
        {
            return _jobs.Take(limit).Select(j => new JobRecord
            {
                Id = j.Id,
                Kind = j.Kind,
                Node = j.Node,
                WouldRunOn = j.WouldRunOn,
                Reason = j.Reason,
                StartedAt = j.StartedAt,
                DurationMs = j.DurationMs,
                Outcome = j.Outcome,
                ExitCode = j.ExitCode,
                Nice = j.Nice,
                Command = j.Command,
                User = j.User,
                Client = j.Client,
                Item = j.Item,
                ItemId = j.ItemId,
                PlaySessionId = j.PlaySessionId,
            }).ToArray();
        }
    }
}

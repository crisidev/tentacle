using System;
using System.Threading;

namespace Tentacle.Broker;

/// <summary>
/// This server as a place to run jobs, next to the tentacles. With slots it competes
/// in least-loaded placement by its weight, as one more node (ties go to whichever
/// took a job least recently); without, it only takes what no tentacle can. Every job that could have gone
/// remote but runs here counts towards its load, whatever the reason.
/// </summary>
public sealed class LocalSeat
{
    private readonly Lock _lock = new();
    private double _load;
    private double _backgroundLoad;

    /// <summary>Gets or sets the slots this server offers in placement; 0 = only what no tentacle can take.</summary>
    public int Slots { get; set; }

    /// <summary>Gets or sets the scheduling weight, as for a tentacle.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Gets the slots background jobs may use here: half, at least one.</summary>
    public int BackgroundSlots => Slots <= 0 ? 0 : Math.Max(1, Slots / 2);

    /// <summary>Gets when this server last took a job, as a placement turn (higher is later).</summary>
    public long LastTurn { get; private set; }

    /// <summary>Gets the cost of the jobs running here.</summary>
    public double Load
    {
        get
        {
            lock (_lock)
            {
                return _load;
            }
        }
    }

    /// <summary>Gets the cost of the background jobs running here.</summary>
    public double BackgroundLoad
    {
        get
        {
            lock (_lock)
            {
                return _backgroundLoad;
            }
        }
    }

    /// <summary>
    /// The score with one more job (lower is better), or null when it does not fit.
    /// </summary>
    /// <param name="cost">The job's cost.</param>
    /// <param name="background">Whether it is a background job.</param>
    /// <returns>The score, or null.</returns>
    public double? ScoreWith(double cost, bool background)
    {
        const double Epsilon = 1e-9;
        lock (_lock)
        {
            if (Slots <= 0 || _load + cost > Slots + Epsilon || (background && _backgroundLoad + cost > BackgroundSlots + Epsilon))
            {
                return null;
            }

            return (_load + cost) / (Weight > 0 ? Weight : 1);
        }
    }

    /// <summary>
    /// Counts a job running here.
    /// </summary>
    /// <param name="cost">The job's cost.</param>
    /// <param name="background">Whether it is a background job.</param>
    /// <returns>The lease to release when it ends.</returns>
    public JobLease Reserve(double cost, bool background)
    {
        lock (_lock)
        {
            _load += cost;
            if (background)
            {
                _backgroundLoad += cost;
            }
        }

        LastTurn = Scheduler.NextTurn();
        return new JobLease(cost, background);
    }

    /// <summary>
    /// Stops counting a job.
    /// </summary>
    /// <param name="lease">The lease.</param>
    public void Release(JobLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_lock)
        {
            _load = Math.Max(0, _load - lease.Cost);
            if (lease.Background)
            {
                _backgroundLoad = Math.Max(0, _backgroundLoad - lease.Cost);
            }
        }
    }
}

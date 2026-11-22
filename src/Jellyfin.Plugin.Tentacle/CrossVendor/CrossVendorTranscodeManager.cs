using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Plugin.Tentacle.CrossVendor;

/// <summary>
/// Jellyfin's transcode manager, unchanged except for one step: before it starts a
/// transcode, <see cref="CrossVendorPlanner"/> may rebuild the command line for a
/// tentacle with another vendor's GPU. Jellyfin then starts the shim with that command
/// line, and the broker sends it to that tentacle.
/// </summary>
public sealed class CrossVendorTranscodeManager : ITranscodeManager
{
    private readonly ITranscodeManager _inner;
    private readonly Lazy<CrossVendorPlanner> _planner;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrossVendorTranscodeManager"/> class.
    /// </summary>
    /// <param name="inner">Jellyfin's own transcode manager.</param>
    /// <param name="planner">The planner, resolved at the first transcode so that building this manager does not build the broker's runtime.</param>
    public CrossVendorTranscodeManager(ITranscodeManager inner, Lazy<CrossVendorPlanner> planner)
    {
        _inner = inner;
        _planner = planner;
    }

    /// <inheritdoc />
    public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null)
    {
        var arguments = commandLineArguments;
        try
        {
            arguments = _planner.Value.Plan(state, outputPath, commandLineArguments, transcodingJobType) ?? commandLineArguments;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Never in the way of playback: the planner logs what it can; start as built.
            arguments = commandLineArguments;
        }

        return _inner.StartFfMpeg(state, outputPath, arguments, userId, transcodingJobType, cancellationTokenSource, workingDirectory);
    }

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string playSessionId) => _inner.GetTranscodingJob(playSessionId);

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => _inner.GetTranscodingJob(path, type);

    /// <inheritdoc />
    public void PingTranscodingJob(string playSessionId, bool? isUserPaused) => _inner.PingTranscodingJob(playSessionId, isUserPaused);

    /// <inheritdoc />
    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles) => _inner.KillTranscodingJobs(deviceId, playSessionId, deleteFiles);

    /// <inheritdoc />
    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
        => _inner.ReportTranscodingProgress(job, state, transcodingPosition, framerate, percentComplete, bytesTranscoded, bitRate);

    /// <inheritdoc />
    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type) => _inner.OnTranscodeBeginRequest(path, type);

    /// <inheritdoc />
    public void OnTranscodeEndRequest(TranscodingJob job) => _inner.OnTranscodeEndRequest(job);

    /// <inheritdoc />
    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken) => _inner.LockAsync(outputPath, cancellationToken);
}

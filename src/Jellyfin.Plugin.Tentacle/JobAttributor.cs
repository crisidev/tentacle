using System;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Tentacle.Broker;
using Tentacle.Protocol;

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Tells the broker who an ffmpeg job is for and what it works on. A playback
/// transcode is found through Jellyfin's own transcoding job (keyed by its output
/// path), whose device leads to the session and its user. Any job's input file
/// leads to the library item.
/// </summary>
public sealed class JobAttributor
{
    private readonly ITranscodeManager _transcodeManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JobAttributor"/> class.
    /// </summary>
    /// <param name="transcodeManager">Jellyfin's transcode manager.</param>
    /// <param name="sessionManager">Jellyfin's session manager.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    public JobAttributor(ITranscodeManager transcodeManager, ISessionManager sessionManager, ILibraryManager libraryManager)
    {
        _transcodeManager = transcodeManager;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// The attribution of a job, or null when nothing is known (yet).
    /// </summary>
    /// <param name="analysis">The job's argv analysis.</param>
    /// <returns>What is known.</returns>
    public JobAttribution? Attribute(ArgvAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var item = analysis.Inputs.Select(FindItem).FirstOrDefault(i => i is not null);

        TranscodingJob? job = null;
        if (analysis.Output is { } output)
        {
            job = _transcodeManager.GetTranscodingJob(output, TranscodingJobType.Hls)
                ?? _transcodeManager.GetTranscodingJob(output, TranscodingJobType.Progressive)
                ?? _transcodeManager.GetTranscodingJob(output, TranscodingJobType.Dash);
        }

        SessionInfo? session = null;
        if (job is not null && !string.IsNullOrEmpty(job.DeviceId))
        {
            var sessions = _sessionManager.Sessions.Where(s => string.Equals(s.DeviceId, job.DeviceId, StringComparison.Ordinal)).ToArray();
            session = sessions.FirstOrDefault(s => job.MediaSource is { } source && string.Equals(s.PlayState?.MediaSourceId, source.Id, StringComparison.OrdinalIgnoreCase))
                ?? sessions.OrderByDescending(s => s.LastActivityDate).FirstOrDefault();
            item ??= session?.FullNowPlayingItem;
        }

        if (item is null && session is null && job is null)
        {
            return null;
        }

        var client = session is null ? null : string.Join(" on ", new[] { session.Client, session.DeviceName }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal));
        return new JobAttribution(
            session?.UserName,
            string.IsNullOrEmpty(client) ? null : client,
            item is null ? null : Describe(item),
            item?.Id.ToString("N", CultureInfo.InvariantCulture),
            job?.PlaySessionId);
    }

    /// <summary>
    /// "Series S01E02: Episode", "Movie (2024)", or the item's name.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>A display name.</returns>
    public static string Describe(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is Episode episode)
        {
            var number = episode.ParentIndexNumber is { } season && episode.IndexNumber is { } index
                ? string.Create(CultureInfo.InvariantCulture, $" S{season:00}E{index:00}")
                : string.Empty;
            return $"{episode.SeriesName}{number}: {episode.Name}";
        }

        // Without online metadata the name is the folder's, often already "Name (2024)".
        var suffix = item.ProductionYear is { } year ? string.Create(CultureInfo.InvariantCulture, $" ({year})") : string.Empty;
        return suffix.Length > 0 && !item.Name.EndsWith(suffix, StringComparison.Ordinal) ? item.Name + suffix : item.Name;
    }

    private BaseItem? FindItem(string path)
    {
        try
        {
            return _libraryManager.FindByPath(path, false);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

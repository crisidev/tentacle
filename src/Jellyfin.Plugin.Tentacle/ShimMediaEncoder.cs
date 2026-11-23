using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Jellyfin's media encoder, unchanged except for one step: once Jellyfin has found
/// and validated its ffmpeg, the shim takes its place. Jellyfin calls
/// <see cref="SetFFmpegPath"/> after the plugin's hosted services start and before
/// it runs any job, so every ffmpeg it starts from then on is the shim.
/// </summary>
public sealed class ShimMediaEncoder : MediaEncoderDecorator
{
    private readonly ShimInstaller _installer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShimMediaEncoder"/> class.
    /// </summary>
    /// <param name="inner">Jellyfin's own media encoder.</param>
    /// <param name="installer">Puts the shim in front of the encoder's ffmpeg.</param>
    public ShimMediaEncoder(IMediaEncoder inner, ShimInstaller installer)
        : base(inner)
    {
        _installer = installer;
    }

    /// <inheritdoc />
    public override bool SetFFmpegPath()
    {
        // Jellyfin validates and interrogates the real ffmpeg; the shim only replaces
        // it once that has succeeded.
        var valid = Inner.SetFFmpegPath();
        if (valid)
        {
            _installer.Redirect(Inner);
        }

        return valid;
    }
}

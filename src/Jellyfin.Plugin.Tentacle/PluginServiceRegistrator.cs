using System;
using System.Linq;
#if NET10_0_OR_GREATER
using Jellyfin.Plugin.Tentacle.CrossVendor;
#endif
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Registers Tentacle's services into Jellyfin's web host container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JobAttributor>();
        serviceCollection.AddSingleton<ShimInstaller>();
        serviceCollection.AddSingleton<TentacleRuntime>();
        serviceCollection.AddHostedService<TentacleHostedService>();
        DecorateMediaEncoder(serviceCollection);
#if NET10_0_OR_GREATER
        DecorateTranscodeManager(serviceCollection);
#endif
    }

    /// <summary>
    /// Wraps Jellyfin's media encoder so the shim replaces its ffmpeg once validated.
    /// Plugins register after Jellyfin's own services, so the encoder is already there.
    /// </summary>
    private static void DecorateMediaEncoder(IServiceCollection services)
    {
        var index = services.ToList().FindLastIndex(d => d.ServiceType == typeof(IMediaEncoder));
        if (index < 0 || services[index] is not { Lifetime: ServiceLifetime.Singleton, ImplementationType: { } implementation })
        {
            // Not the registration Tentacle knows: leave it, the dashboard says the shim is off.
            return;
        }

        // The container still builds (and disposes) Jellyfin's encoder; consumers get the wrapper.
        services.AddSingleton(implementation);
        services[index] = ServiceDescriptor.Singleton<IMediaEncoder>(sp =>
            new ShimMediaEncoder((IMediaEncoder)sp.GetRequiredService(implementation), sp.GetRequiredService<ShimInstaller>()));
    }

#if NET10_0_OR_GREATER

    /// <summary>
    /// Wraps Jellyfin's transcode manager so a transcode may be rebuilt for a tentacle
    /// with another vendor's GPU before it starts (Jellyfin 12.1 and later: the
    /// EncodingHelper calls this relies on are not the same on 10.11).
    /// </summary>
    private static void DecorateTranscodeManager(IServiceCollection services)
    {
        var index = services.ToList().FindLastIndex(d => d.ServiceType == typeof(ITranscodeManager));
        if (index < 0 || services[index] is not { Lifetime: ServiceLifetime.Singleton, ImplementationType: { } implementation })
        {
            // Not the registration Tentacle knows: transcodes run as Jellyfin builds them.
            return;
        }

        services.AddSingleton<CrossVendorPlanner>();
        services.AddSingleton(implementation);
        services[index] = ServiceDescriptor.Singleton<ITranscodeManager>(sp => new CrossVendorTranscodeManager(
            (ITranscodeManager)sp.GetRequiredService(implementation),
            new Lazy<CrossVendorPlanner>(sp.GetRequiredService<CrossVendorPlanner>)));
    }
#endif
}

using System.Linq;
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
}

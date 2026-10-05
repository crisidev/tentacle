using MediaBrowser.Controller;
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
        serviceCollection.AddSingleton<TentacleRuntime>();
        serviceCollection.AddHostedService<TentacleHostedService>();
    }
}

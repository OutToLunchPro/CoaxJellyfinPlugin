using Jellyfin.Plugin.Coax.Indexing;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Coax;

/// <summary>
/// Registers Coax services into Jellyfin's DI container. Jellyfin discovers and invokes this
/// automatically on startup. Registering <see cref="CoaxIndexBuilder"/> here lets the
/// controller take it via constructor injection instead of allocating one per request.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Scoped: it resolves a per-request DbContext via the factory and holds no state
        // across requests, matching the controller's per-request lifetime.
        serviceCollection.AddScoped<CoaxIndexBuilder>();
    }
}

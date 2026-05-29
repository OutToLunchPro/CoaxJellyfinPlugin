using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Coax.Configuration;

/// <summary>
/// Plugin configuration. Intentionally empty for v1 — the index endpoints are stateless
/// and take all their parameters from the request body.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
}

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Coax.Models;

/// <summary>
/// Capability probe response for <c>GET /coax/info</c>.
/// </summary>
public class InfoResponse
{
    /// <summary>Gets or sets the plugin semantic version.</summary>
    [JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; set; } = "1.0.0";

    /// <summary>Gets or sets the data-contract version the client feature-gates on.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; set; } = CoaxContract.Version;

    /// <summary>Gets or sets the supported capability identifiers.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; set; } = CoaxContract.Capabilities;
}

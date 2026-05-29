using System.Collections.Generic;

namespace Jellyfin.Plugin.Coax.Models;

/// <summary>
/// Shared contract constants for the Coax data endpoints.
/// </summary>
public static class CoaxContract
{
    /// <summary>The data-contract version both sides agree on.</summary>
    public const int Version = 1;

    /// <summary>Capability identifiers advertised by <c>GET /coax/info</c>.</summary>
    public static readonly IReadOnlyList<string> Capabilities = new[]
    {
        "index.people",
        "index.studios",
        "index.items"
    };
}

using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.Coax.Models;

/// <summary>
/// Shared contract constants for the Coax data endpoints. Backs the API models
/// (<c>IndexRequest</c>, <c>InfoResponse</c>) and the capability probe so every
/// surface advertises one authoritative version, ceiling, and capability set.
/// </summary>
public static class CoaxContract
{
    /// <summary>The current data-contract version this plugin emits.</summary>
    public const int Version = 1;

    /// <summary>
    /// The oldest contract version still accepted on <c>POST /coax/index</c>. The endpoint
    /// admits any client whose version is &gt;= this value (additive minor bumps stay
    /// compatible) instead of demanding an exact match against <see cref="Version"/>.
    /// </summary>
    public const int MinSupportedVersion = 1;

    /// <summary>
    /// Hard upper bound on the <c>maxItems</c> shaping knob. Client requests are clamped to
    /// this ceiling so a single index call can never be coerced into materializing an
    /// unbounded result set.
    /// </summary>
    public const int DefaultMaxItemsCeiling = 10000;

    /// <summary>
    /// Capability identifiers advertised by <c>GET /coax/info</c>. Wrapped in a
    /// <see cref="ReadOnlyCollection{T}"/> so neither the reference nor its elements can be
    /// mutated at runtime, yet it implements <see cref="System.Collections.Generic.IReadOnlyList{T}"/>
    /// directly for the <c>InfoResponse</c> contract. Built once at type initialization, so
    /// the capability probe serializes this instance with no per-heartbeat allocation.
    /// </summary>
    public static readonly ReadOnlyCollection<string> Capabilities = new(new[]
    {
        "inverse-people-join",
        "shaping-max-items",
        "shaping-episode-marathon"
    });
}

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Coax.Models;

/// <summary>
/// Request body for <c>POST /coax/index</c>. Shapes data only — never scheduling.
/// </summary>
public class IndexRequest
{
    /// <summary>Gets or sets the contract version the client speaks.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; set; } = CoaxContract.Version;

    /// <summary>Gets or sets the Jellyfin library ids this lineup draws from.</summary>
    [JsonPropertyName("libraryIds")]
    public IReadOnlyList<string> LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the schedulable item types to return (<c>Movie</c> and/or <c>Episode</c>).</summary>
    [JsonPropertyName("itemTypes")]
    public IReadOnlyList<string> ItemTypes { get; set; } = new[] { "Movie", "Episode" };

    /// <summary>Gets or sets which sections to include (<c>items</c>, <c>people</c>).</summary>
    [JsonPropertyName("include")]
    public IReadOnlyList<string> Include { get; set; } = new[] { "items", "people" };

    /// <summary>Gets or sets the server-evaluated filters.</summary>
    [JsonPropertyName("filters")]
    public IndexFilters? Filters { get; set; }

    /// <summary>Gets or sets the payload-shaping parameters.</summary>
    [JsonPropertyName("shaping")]
    public IndexShaping? Shaping { get; set; }
}

/// <summary>
/// Server-evaluated content filters. Returned data already obeys these.
/// </summary>
public class IndexFilters
{
    /// <summary>Gets or sets an optional content-rating cap (e.g. <c>PG-13</c>). Null = no cap.</summary>
    [JsonPropertyName("maxOfficialRating")]
    public string? MaxOfficialRating { get; set; }

    /// <summary>Gets or sets the watched filter: <c>all</c> | <c>watched</c> | <c>unwatched</c>.</summary>
    [JsonPropertyName("watched")]
    public string Watched { get; set; } = "all";

    /// <summary>Gets or sets the Jellyfin user id. Required iff <see cref="Watched"/> is not <c>all</c>.</summary>
    [JsonPropertyName("userId")]
    public string? UserId { get; set; }
}

/// <summary>
/// Payload-size shaping. None of these change scheduling — only how much data is returned.
/// </summary>
public class IndexShaping
{
    /// <summary>Gets or sets the minimum item count for a person to be emitted. Default 1.</summary>
    [JsonPropertyName("minItemsPerPerson")]
    public int MinItemsPerPerson { get; set; } = 1;

    /// <summary>Gets or sets the global hard cap on returned items. Null = no cap.</summary>
    [JsonPropertyName("maxItems")]
    public int? MaxItems { get; set; }

    /// <summary>Gets or sets the per-series episode cap. Null = every episode.</summary>
    [JsonPropertyName("maxEpisodesPerSeries")]
    public int? MaxEpisodesPerSeries { get; set; }
}

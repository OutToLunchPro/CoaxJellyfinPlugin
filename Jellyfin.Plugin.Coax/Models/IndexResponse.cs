using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Coax.Models;

/// <summary>
/// Response body for <c>POST /coax/index</c>.
/// </summary>
public class IndexResponse
{
    /// <summary>Gets or sets the contract version of this response.</summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; set; } = CoaxContract.Version;

    /// <summary>Gets or sets a value indicating whether maxItems or maxEpisodesPerSeries forced a subset.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>Gets or sets the filtered schedulable items (movies and/or episodes).</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<ItemDto> Items { get; set; } = new List<ItemDto>();

    /// <summary>Gets or sets the person→items inverse (the reason this plugin exists).</summary>
    [JsonPropertyName("people")]
    public IReadOnlyList<PersonDto> People { get; set; } = new List<PersonDto>();

    /// <summary>Gets or sets collection memberships intersected with the returned items.</summary>
    [JsonPropertyName("collections")]
    public IReadOnlyList<CollectionDto> Collections { get; set; } = new List<CollectionDto>();
}

/// <summary>
/// A single schedulable item. Episode-only fields are omitted for movies.
/// </summary>
public class ItemDto
{
    /// <summary>Gets or sets the item id (32-char hex, matching the Jellyfin API item id).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the item display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type (<c>Movie</c> or <c>Episode</c>).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year.</summary>
    [JsonPropertyName("productionYear")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the runtime in Jellyfin ticks (100ns units).</summary>
    [JsonPropertyName("runTimeTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets the genres (inherited from the series for episodes).</summary>
    [JsonPropertyName("genres")]
    public IReadOnlyList<string> Genres { get; set; } = System.Array.Empty<string>();

    /// <summary>Gets or sets the official content rating.</summary>
    [JsonPropertyName("officialRating")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets the date the item was added to the library (ISO 8601 UTC).</summary>
    [JsonPropertyName("dateCreated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateCreated { get; set; }

    /// <summary>Gets or sets the premiere/air date (ISO 8601 UTC).</summary>
    [JsonPropertyName("premiereDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PremiereDate { get; set; }

    /// <summary>Gets or sets the studios.</summary>
    [JsonPropertyName("studios")]
    public IReadOnlyList<string> Studios { get; set; } = System.Array.Empty<string>();

    // --- Episode-only fields (null/omitted for movies) ---

    /// <summary>Gets or sets the parent series id (→ grandparentRatingKey).</summary>
    [JsonPropertyName("seriesId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets the parent series name.</summary>
    [JsonPropertyName("seriesName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the parent season id (→ parentRatingKey).</summary>
    [JsonPropertyName("seasonId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeasonId { get; set; }

    /// <summary>Gets or sets the season number (→ parentIndex).</summary>
    [JsonPropertyName("parentIndexNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ParentIndexNumber { get; set; }

    /// <summary>Gets or sets the episode number within the season.</summary>
    [JsonPropertyName("indexNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IndexNumber { get; set; }
}

/// <summary>
/// A person and the schedulable item ids they are associated with. Item ids are always
/// Movie or Episode ids — never Series ids — so the client's person→items map is uniform.
/// </summary>
public class PersonDto
{
    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the role: <c>Actor</c> or <c>Director</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the associated item ids.</summary>
    [JsonPropertyName("itemIds")]
    public IReadOnlyList<string> ItemIds { get; set; } = System.Array.Empty<string>();
}

/// <summary>
/// A collection (BoxSet) and its members intersected with the returned items.
/// </summary>
public class CollectionDto
{
    /// <summary>Gets or sets the collection name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the member item ids present in the returned set.</summary>
    [JsonPropertyName("itemIds")]
    public IReadOnlyList<string> ItemIds { get; set; } = System.Array.Empty<string>();
}

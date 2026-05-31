using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Coax.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Coax.Indexing;

/// <summary>
/// Stateless builder that turns an <see cref="IndexRequest"/> into an <see cref="IndexResponse"/>.
/// Runs the person→items inverse (and collection membership join) that vanilla Jellyfin can't
/// produce cheaply, entirely in-process against the library DB.
/// </summary>
public class CoaxIndexBuilder
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILocalizationManager _localization;
    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory;
    private readonly ILogger<CoaxIndexBuilder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoaxIndexBuilder"/> class.
    /// </summary>
    public CoaxIndexBuilder(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILocalizationManager localization,
        IDbContextFactory<JellyfinDbContext> dbFactory,
        ILogger<CoaxIndexBuilder> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _localization = localization;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Builds the index for the given request, scoped to the authenticated caller.
    /// </summary>
    /// <param name="request">The validated request.</param>
    /// <param name="accessUser">
    /// The authenticated caller. Every library query runs as this user so Jellyfin enforces
    /// their access + parental/tag rules — even when no watch filter is applied. The
    /// controller is responsible for authorizing any other user named in <c>filters.userId</c>
    /// before calling in.
    /// </param>
    /// <returns>The computed response.</returns>
    public IndexResponse Build(IndexRequest request, User accessUser)
    {
        ArgumentNullException.ThrowIfNull(accessUser);

        var filters = request.Filters ?? new IndexFilters();
        var shaping = request.Shaping ?? new IndexShaping();

        var includeItems = request.Include.Contains("items", StringComparer.OrdinalIgnoreCase);
        var includePeople = request.Include.Contains("people", StringComparer.OrdinalIgnoreCase);

        var (isPlayed, user) = ResolveWatched(filters, accessUser);
        var ratingCap = ResolveRatingCap(filters.MaxOfficialRating);
        var itemKinds = ResolveItemKinds(request.ItemTypes);

        // 1. Gather the raw filtered library across the requested libraries.
        var rawItems = GatherItems(request.LibraryIds, itemKinds, user, isPlayed, ratingCap);

        // 2. Payload shaping (per-series cap, then global cap). Tracks truncation.
        var truncated = false;
        var shaped = ApplyEpisodeCap(rawItems, shaping.MaxEpisodesPerSeries, ref truncated);
        shaped = ApplyMaxItems(shaped, shaping.MaxItems, ref truncated);

        var includedIds = new HashSet<Guid>(shaped.Select(i => i.Id));

        var response = new IndexResponse { Truncated = truncated };

        if (includeItems)
        {
            response.Items = shaped.Select(BuildItemDto).ToList();
        }

        if (includePeople)
        {
            response.People = BuildPeople(shaped, includedIds, Math.Max(1, shaping.MinItemsPerPerson));
        }

        return response;
    }

    // ---- Filters / resolution ----------------------------------------------------------

    private (bool? IsPlayed, User? User) ResolveWatched(IndexFilters filters, User accessUser)
    {
        var watched = (filters.Watched ?? "all").Trim().ToLowerInvariant();
        if (watched == "all")
        {
            // No watch-state filter, but still run the query AS the caller so library
            // access and parental/tag rules are enforced. A null user here would return
            // the entire server unrestricted — the bypass this guards against.
            return (null, accessUser);
        }

        if (string.IsNullOrWhiteSpace(filters.UserId))
        {
            throw new ArgumentException("filters.userId is required when filters.watched is not \"all\".");
        }

        if (!Guid.TryParse(filters.UserId, out var userGuid))
        {
            throw new ArgumentException($"filters.userId is not a valid id: {filters.UserId}");
        }

        var user = _userManager.GetUserById(userGuid)
            ?? throw new ArgumentException($"No such user: {filters.UserId}");

        return watched switch
        {
            "watched" => (true, user),
            "unwatched" => (false, user),
            _ => throw new ArgumentException($"filters.watched must be all|watched|unwatched, got: {filters.Watched}")
        };
    }

    private int? ResolveRatingCap(string? maxOfficialRating)
    {
        if (string.IsNullOrWhiteSpace(maxOfficialRating))
        {
            return null;
        }

        // Higher score = more restrictive. Null means we couldn't map it → treat as no cap.
        return RatingScore(maxOfficialRating);
    }

    // Jellyfin 10.11 replaced GetRatingLevel(rating) with GetRatingScore(rating, country)
    // returning a ParentalRatingScore. We compare on Score; null country uses the server default.
    private int? RatingScore(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        return _localization.GetRatingScore(rating, null!)?.Score;
    }

    private static IReadOnlyList<BaseItemKind> ResolveItemKinds(IReadOnlyList<string> itemTypes)
    {
        var kinds = new List<BaseItemKind>();
        foreach (var t in itemTypes)
        {
            if (string.Equals(t, "Movie", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add(BaseItemKind.Movie);
            }
            else if (string.Equals(t, "Episode", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add(BaseItemKind.Episode);
            }
        }

        if (kinds.Count == 0)
        {
            kinds.Add(BaseItemKind.Movie);
            kinds.Add(BaseItemKind.Episode);
        }

        return kinds;
    }

    private bool PassesRating(BaseItem item, int? ratingCap)
    {
        if (ratingCap is null)
        {
            return true;
        }

        var rating = item.OfficialRating;
        if (string.IsNullOrWhiteSpace(rating))
        {
            // Unrated: ambiguous. Include (lenient) — mirrors JF not blocking unrated by default.
            return true;
        }

        var level = RatingScore(rating);
        // Unknown rating string → can't compare → include.
        return level is null || level.Value <= ratingCap.Value;
    }

    // ---- Item gathering ----------------------------------------------------------------

    private List<BaseItem> GatherItems(
        IReadOnlyList<string> libraryIds,
        IReadOnlyList<BaseItemKind> kinds,
        User? user,
        bool? isPlayed,
        int? ratingCap)
    {
        var seen = new HashSet<Guid>();
        var result = new List<BaseItem>();
        var kindArray = kinds.ToArray();

        foreach (var libraryId in libraryIds)
        {
            if (!Guid.TryParse(libraryId, out var libGuid))
            {
                throw new ArgumentException($"Invalid libraryId: {libraryId}");
            }

            var query = new InternalItemsQuery(user)
            {
                Recursive = true,
                ParentId = libGuid,
                IncludeItemTypes = kindArray,
                IsPlayed = isPlayed,
                EnableTotalRecordCount = false
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (seen.Add(item.Id) && PassesRating(item, ratingCap))
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }

    // ---- Shaping -----------------------------------------------------------------------

    private static List<BaseItem> ApplyEpisodeCap(List<BaseItem> items, int? maxEpisodesPerSeries, ref bool truncated)
    {
        if (maxEpisodesPerSeries is null || maxEpisodesPerSeries.Value <= 0)
        {
            return items;
        }

        var cap = maxEpisodesPerSeries.Value;
        var kept = new List<BaseItem>(items.Count);
        var episodesBySeries = new Dictionary<Guid, List<Episode>>();

        foreach (var item in items)
        {
            if (item is Episode ep)
            {
                if (!episodesBySeries.TryGetValue(ep.SeriesId, out var list))
                {
                    list = new List<Episode>();
                    episodesBySeries[ep.SeriesId] = list;
                }

                list.Add(ep);
            }
            else
            {
                kept.Add(item);
            }
        }

        foreach (var (_, episodes) in episodesBySeries)
        {
            if (episodes.Count <= cap)
            {
                kept.AddRange(episodes);
                continue;
            }

            // Contiguous run by (season, episode) starting at a random offset so a
            // client-side marathon is still possible.
            var ordered = episodes
                .OrderBy(e => e.ParentIndexNumber ?? 0)
                .ThenBy(e => e.IndexNumber ?? 0)
                .ToList();

            var maxStart = ordered.Count - cap;
            var start = Random.Shared.Next(0, maxStart + 1);
            kept.AddRange(ordered.GetRange(start, cap));
            truncated = true;
        }

        return kept;
    }

    internal static List<BaseItem> ApplyMaxItems(List<BaseItem> items, int? maxItems, ref bool truncated)
    {
        // The ceiling is an unconditional backstop: a request with no cap (null/<=0) or a cap
        // above the ceiling is still clamped, so a single call can never be coerced into
        // materializing an unbounded result set.
        var effectiveCap = maxItems is > 0
            ? Math.Min(maxItems.Value, CoaxContract.DefaultMaxItemsCeiling)
            : CoaxContract.DefaultMaxItemsCeiling;

        if (items.Count <= effectiveCap)
        {
            return items;
        }

        truncated = true;
        // Random subset — order is irrelevant to the client (it re-shuffles), so a shuffle suffices.
        return items.OrderBy(_ => Random.Shared.Next()).Take(effectiveCap).ToList();
    }

    // ---- Item DTOs ---------------------------------------------------------------------

    private static ItemDto BuildItemDto(BaseItem item)
    {
        var dto = new ItemDto
        {
            Id = item.Id.ToString("N"),
            Name = item.Name ?? string.Empty,
            ProductionYear = item.ProductionYear,
            RunTimeTicks = item.RunTimeTicks,
            Genres = item.Genres ?? Array.Empty<string>(),
            OfficialRating = item.OfficialRating,
            DateCreated = FormatDate(item.DateCreated),
            PremiereDate = FormatDate(item.PremiereDate),
            Studios = item.Studios ?? Array.Empty<string>()
        };

        if (item is Episode ep)
        {
            dto.Type = "Episode";
            dto.SeriesId = ep.SeriesId == Guid.Empty ? null : ep.SeriesId.ToString("N");
            dto.SeriesName = ep.SeriesName;
            dto.SeasonId = ep.SeasonId is { } sid && sid != Guid.Empty ? sid.ToString("N") : null;
            dto.ParentIndexNumber = ep.ParentIndexNumber;
            dto.IndexNumber = ep.IndexNumber;

            // Episodes inherit genres from the series; the episode DTO often carries none.
            if (dto.Genres.Count == 0 && ep.SeriesId != Guid.Empty)
            {
                var series = ep.Series;
                if (series?.Genres is { Length: > 0 } seriesGenres)
                {
                    dto.Genres = seriesGenres;
                }
            }
        }
        else
        {
            dto.Type = "Movie";
        }

        return dto;
    }

    private static string? FormatDate(DateTime value)
    {
        return value == default
            ? null
            : value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }

    private static string? FormatDate(DateTime? value)
    {
        return value is { } v ? FormatDate(v) : null;
    }

    // ---- People (the whole reason this plugin exists) ----------------------------------

    /// <summary>
    /// Builds the person→items inverse with a single chunked join over the PeopleBaseItemMap
    /// table — the query vanilla can't do cheaply — instead of an N+1 GetPeople per item.
    /// </summary>
    private IReadOnlyList<PersonDto> BuildPeople(
        List<BaseItem> items,
        HashSet<Guid> includedIds,
        int minItemsPerPerson)
    {
        // For TV: map each series to its returned episode ids so series-level cast can be
        // expanded onto every returned episode of that series.
        var episodesBySeries = new Dictionary<Guid, List<Guid>>();
        foreach (var item in items)
        {
            if (item is Episode ep && ep.SeriesId != Guid.Empty)
            {
                if (!episodesBySeries.TryGetValue(ep.SeriesId, out var list))
                {
                    list = new List<Guid>();
                    episodesBySeries[ep.SeriesId] = list;
                }

                list.Add(item.Id);
            }
        }

        // One map covering the schedulable items themselves plus their parent series.
        var lookupIds = new HashSet<Guid>(includedIds);
        foreach (var seriesId in episodesBySeries.Keys)
        {
            lookupIds.Add(seriesId);
        }

        var buckets = new Dictionary<(string Name, string Role), HashSet<Guid>>();

        foreach (var row in QueryPeopleMap(lookupIds))
        {
            var role = NormalizeRole(row.PersonType);
            if (role is null)
            {
                continue;
            }

            var key = (NormalizeName(row.Name), role);

            if (includedIds.Contains(row.ItemId))
            {
                // Item-level credit: movie, or an episode's own cast / guest star / director.
                AddToBucket(buckets, key, new[] { row.ItemId });
            }
            else if (role == "Actor" && episodesBySeries.TryGetValue(row.ItemId, out var episodeIds))
            {
                // Series-level cast → all returned episodes of that series.
                // Directors are never inherited from the series (episode-level only).
                AddToBucket(buckets, key, episodeIds);
            }
        }

        return buckets
            .Where(kv => kv.Value.Count >= minItemsPerPerson)
            .Select(kv => new PersonDto
            {
                Name = kv.Key.Name,
                Type = kv.Key.Role,
                ItemIds = kv.Value.Select(id => id.ToString("N")).ToList()
            })
            .ToList();
    }

    // Pull (ItemId, PersonName, PersonType) for the given items in chunks — a handful of
    // queries instead of one per item, and staying under SQLite's bound-parameter limit.
    private List<(Guid ItemId, string? Name, string? PersonType)> QueryPeopleMap(IReadOnlyCollection<Guid> ids)
    {
        var rows = new List<(Guid, string?, string?)>(ids.Count);
        using var db = _dbFactory.CreateDbContext();

        foreach (var chunk in ids.Chunk(500))
        {
            var batch = db.PeopleBaseItemMap
                .Where(m => chunk.Contains(m.ItemId))
                .Select(m => new { m.ItemId, m.People.Name, m.People.PersonType })
                .ToList();

            foreach (var r in batch)
            {
                rows.Add((r.ItemId, r.Name, r.PersonType));
            }
        }

        return rows;
    }

    // Jellyfin stores PersonType as a string. GuestStar folds into Actor; everything other
    // than Actor/Director is ignored for v1.
    private static string? NormalizeRole(string? personType)
    {
        if (string.Equals(personType, "Actor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(personType, "GuestStar", StringComparison.OrdinalIgnoreCase))
        {
            return "Actor";
        }

        if (string.Equals(personType, "Director", StringComparison.OrdinalIgnoreCase))
        {
            return "Director";
        }

        return null;
    }

    private static void AddToBucket(
        Dictionary<(string Name, string Role), HashSet<Guid>> buckets,
        (string Name, string Role) key,
        IEnumerable<Guid> ids)
    {
        if (!buckets.TryGetValue(key, out var set))
        {
            set = new HashSet<Guid>();
            buckets[key] = set;
        }

        foreach (var id in ids)
        {
            set.Add(id);
        }
    }

    private static string NormalizeName(string? name) => (name ?? string.Empty).Trim();
}

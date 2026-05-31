using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Coax.Indexing;
using Jellyfin.Plugin.Coax.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Coax.Tests;

/// <summary>
/// Focused tests for <see cref="CoaxIndexBuilder"/>: the primary genre-resolution path
/// (the series-genre cache) plus the security-relevant filters — rating-cap enforcement
/// and watched/library input validation. Not full coverage by design.
///
/// Every test runs with <c>include = ["items"]</c> so only <see cref="ILibraryManager"/>
/// (and, for rating tests, <see cref="ILocalizationManager"/>) is exercised — the people
/// path and its DB context are never touched.
/// </summary>
public class CoaxIndexBuilderTests
{
    private static readonly string LibraryId = Guid.NewGuid().ToString("N");

    // ---- Harness -----------------------------------------------------------------------

    private sealed class Harness
    {
        public Mock<ILibraryManager> Library { get; } = new(MockBehavior.Strict);

        public Mock<IUserManager> Users { get; } = new();

        public Mock<ILocalizationManager> Localization { get; } = new();

        private readonly Dictionary<Guid, BaseItem> _byId = new();

        public Harness WithLibraryItems(params BaseItem[] items)
        {
            Library
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(items.ToList());
            return this;
        }

        // Registers a series resolvable via GetItemById — the hydrated lookup the cache uses.
        public Harness WithSeries(Series series)
        {
            _byId[series.Id] = series;
            Library
                .Setup(x => x.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) => _byId.TryGetValue(id, out var b) ? b : null);
            return this;
        }

        // Maps a content-rating string to a score; unknown strings resolve to null (unrankable).
        public Harness WithRatingScores(Dictionary<string, int> scores)
        {
            Localization
                .Setup(x => x.GetRatingScore(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string rating, string _) =>
                    scores.TryGetValue(rating, out var s) ? new ParentalRatingScore(s, null) : null);
            return this;
        }

        public CoaxIndexBuilder Build() => new(
            Library.Object,
            Users.Object,
            Localization.Object,
            Mock.Of<IDbContextFactory<JellyfinDbContext>>(),
            NullLogger<CoaxIndexBuilder>.Instance);

        // A fixed authenticated caller. The builder uses the access user only to scope the
        // (mocked) library query, so any non-null user is sufficient for these unit tests.
        private static readonly User Caller = new("coax-test", "Default", "Default");

        // Runs the builder as the fixed caller, mirroring how the controller invokes it.
        public IndexResponse Run(IndexRequest request) => Build().Build(request, Caller);
    }

    private static IndexRequest ItemsRequest(IndexFilters? filters = null) => new()
    {
        LibraryIds = new[] { LibraryId },
        Include = new[] { "items" },
        Filters = filters
    };

    private static Movie Movie(string name, string? rating = null, string[]? genres = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OfficialRating = rating,
        Genres = genres ?? Array.Empty<string>()
    };

    private static Episode Episode(Guid seriesId, string[]? genres = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "ep",
        SeriesId = seriesId,
        Genres = genres ?? Array.Empty<string>()
    };

    // ---- Primary path: series-genre cache ----------------------------------------------

    [Fact]
    public void Episode_WithoutGenres_InheritsFromSeries_FetchedOnce()
    {
        var seriesId = Guid.NewGuid();
        var harness = new Harness()
            .WithLibraryItems(Episode(seriesId))
            .WithSeries(new Series { Id = seriesId, Genres = new[] { "Drama", "Thriller" } });

        var response = harness.Run(ItemsRequest());

        var dto = Assert.Single(response.Items);
        Assert.Equal(new[] { "Drama", "Thriller" }, dto.Genres);
        harness.Library.Verify(x => x.GetItemById(seriesId), Times.Once());
    }

    [Fact]
    public void Episode_WithOwnGenres_KeepsThem_AndNeverFetchesSeries()
    {
        var seriesId = Guid.NewGuid();
        var harness = new Harness()
            .WithLibraryItems(Episode(seriesId, genres: new[] { "Comedy" }))
            .WithSeries(new Series { Id = seriesId, Genres = new[] { "Drama" } });

        var response = harness.Run(ItemsRequest());

        var dto = Assert.Single(response.Items);
        Assert.Equal(new[] { "Comedy" }, dto.Genres);
        // An episode that already carries genres must not trigger a series roundtrip.
        harness.Library.Verify(x => x.GetItemById(It.IsAny<Guid>()), Times.Never());
    }

    [Fact]
    public void MultipleEpisodes_SameSeries_ResolveSeriesExactlyOnce()
    {
        var seriesId = Guid.NewGuid();
        var harness = new Harness()
            .WithLibraryItems(Episode(seriesId), Episode(seriesId), Episode(seriesId))
            .WithSeries(new Series { Id = seriesId, Genres = new[] { "Drama" } });

        var response = harness.Run(ItemsRequest());

        Assert.Equal(3, response.Items.Count);
        Assert.All(response.Items, dto => Assert.Equal(new[] { "Drama" }, dto.Genres));
        // The whole point of the cache: one lookup, not one-per-episode (no N+1).
        harness.Library.Verify(x => x.GetItemById(seriesId), Times.Once());
    }

    [Fact]
    public void Series_WithNoGenres_LeavesEpisodesEmpty_AndIsNotRefetched()
    {
        var seriesId = Guid.NewGuid();
        var harness = new Harness()
            .WithLibraryItems(Episode(seriesId), Episode(seriesId))
            .WithSeries(new Series { Id = seriesId, Genres = Array.Empty<string>() });

        var response = harness.Run(ItemsRequest());

        Assert.All(response.Items, dto => Assert.Empty(dto.Genres));
        // Sentinel caching: a genre-less series is probed once, not once per episode.
        harness.Library.Verify(x => x.GetItemById(seriesId), Times.Once());
    }

    [Fact]
    public void Movie_GenresPreserved_AndNoSeriesLookup()
    {
        var harness = new Harness()
            .WithLibraryItems(Movie("Heat", genres: new[] { "Crime" }));

        var response = harness.Run(ItemsRequest());

        var dto = Assert.Single(response.Items);
        Assert.Equal("Movie", dto.Type);
        Assert.Equal(new[] { "Crime" }, dto.Genres);
        harness.Library.Verify(x => x.GetItemById(It.IsAny<Guid>()), Times.Never());
    }

    // ---- Security: rating-cap enforcement (parental control) ---------------------------

    [Fact]
    public void RatingCap_ExcludesItemsAboveCap()
    {
        var harness = new Harness()
            .WithLibraryItems(Movie("Kids", rating: "G"), Movie("Gore", rating: "R"))
            .WithRatingScores(new() { ["G"] = 1, ["PG-13"] = 5, ["R"] = 9 });

        var response = harness.Run(
            ItemsRequest(new IndexFilters { MaxOfficialRating = "PG-13" }));

        var dto = Assert.Single(response.Items);
        Assert.Equal("Kids", dto.Name);
    }

    [Fact]
    public void RatingCap_IncludesItemsAtOrBelowCap()
    {
        var harness = new Harness()
            .WithLibraryItems(Movie("AtCap", rating: "PG-13"), Movie("Below", rating: "G"))
            .WithRatingScores(new() { ["G"] = 1, ["PG-13"] = 5 });

        var response = harness.Run(
            ItemsRequest(new IndexFilters { MaxOfficialRating = "PG-13" }));

        Assert.Equal(2, response.Items.Count);
    }

    [Fact]
    public void RatingCap_IncludesUnratedItems_LenientByDesign()
    {
        // Pins the deliberate lenient policy: an item with no OfficialRating is not blocked
        // by a cap. If this ever becomes fail-closed, this test should change consciously.
        var harness = new Harness()
            .WithLibraryItems(Movie("NoRating", rating: null))
            .WithRatingScores(new() { ["PG-13"] = 5 });

        var response = harness.Run(
            ItemsRequest(new IndexFilters { MaxOfficialRating = "PG-13" }));

        Assert.Single(response.Items);
    }

    // ---- Security: watched/userId input validation -------------------------------------

    [Theory]
    [InlineData(null)]        // watched filter requested without a user
    [InlineData("")]
    [InlineData("not-a-guid")] // malformed user id
    public void Watched_WithMissingOrInvalidUserId_Throws(string? userId)
    {
        var harness = new Harness(); // GetItemList must never be reached.
        var request = ItemsRequest(new IndexFilters { Watched = "watched", UserId = userId });

        Assert.Throws<ArgumentException>(() => harness.Run(request));
    }

    [Fact]
    public void Watched_WithUnknownUser_Throws()
    {
        // Valid GUID, but the user manager resolves nobody → must reject, not fall open.
        var harness = new Harness();
        harness.Users.Setup(x => x.GetUserById(It.IsAny<Guid>())).Returns((User?)null);
        var request = ItemsRequest(new IndexFilters
        {
            Watched = "watched",
            UserId = Guid.NewGuid().ToString("N")
        });

        Assert.Throws<ArgumentException>(() => harness.Run(request));
    }

    // ---- Security: library id validation -----------------------------------------------

    [Fact]
    public void InvalidLibraryId_Throws()
    {
        var harness = new Harness();
        var request = new IndexRequest
        {
            LibraryIds = new[] { "not-a-guid" },
            Include = new[] { "items" }
        };

        Assert.Throws<ArgumentException>(() => harness.Run(request));
    }
}

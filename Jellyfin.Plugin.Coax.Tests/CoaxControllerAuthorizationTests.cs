using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Coax.Api;
using Jellyfin.Plugin.Coax.Indexing;
using Jellyfin.Plugin.Coax.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Coax.Tests;

/// <summary>
/// Authorization tests for <see cref="CoaxController.PostIndex"/> — the boundary that closes
/// the watch-history and library-scrape bypass. Every denial path is asserted to fail closed
/// (403/400) and never reach the index builder.
/// </summary>
public class CoaxControllerAuthorizationTests
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    // ---- The three requested 403 paths -------------------------------------------------

    [Fact]
    public void PostIndex_UnresolvablePrincipal_Returns403()
    {
        // [Authorize] passed but the principal carries no resolvable Jellyfin user.
        var harness = new Harness(caller: null);
        var result = harness.Controller.PostIndex(Request(harness.LibraryId));

        Assert.IsType<ForbidResult>(result.Result);
        harness.AssertBuilderNeverRan();
    }

    [Fact]
    public void PostIndex_NonAdminRequestsAnotherUsersWatchData_Returns403()
    {
        var caller = MakeUser(admin: false);
        var harness = new Harness(caller);

        var request = Request(harness.LibraryId, filterUserId: Guid.NewGuid().ToString("N"));
        var result = harness.Controller.PostIndex(request);

        Assert.IsType<ForbidResult>(result.Result);
        harness.AssertBuilderNeverRan();
    }

    [Fact]
    public void PostIndex_NonAdminRequestsInaccessibleLibrary_Returns403()
    {
        var caller = MakeUser(admin: false);
        var harness = new Harness(caller);
        harness.LibraryReturns(new StubLibrary(visible: false));

        var result = harness.Controller.PostIndex(Request(harness.LibraryId));

        Assert.IsType<ForbidResult>(result.Result);
        harness.AssertBuilderNeverRan();
    }

    // ---- Admin override: the escape hatch must still work --------------------------------

    [Fact]
    public void PostIndex_AdminReadsAnotherUserAcrossAnyLibrary_Proceeds()
    {
        var admin = MakeUser(admin: true);
        var harness = new Harness(admin);
        // Even a library that reports itself invisible is reachable by an admin.
        harness.LibraryReturns(new StubLibrary(visible: false));
        harness.IndexReturnsNoItems();

        var request = Request(
            harness.LibraryId,
            filterUserId: Guid.NewGuid().ToString("N"),
            includeNothing: true);
        var result = harness.Controller.PostIndex(request);

        Assert.Null(result.Result);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public void PostIndex_NonAdminReadsOwnDataFromVisibleLibrary_Proceeds()
    {
        var caller = MakeUser(admin: false);
        var harness = new Harness(caller);
        harness.LibraryReturns(new StubLibrary(visible: true));
        harness.IndexReturnsNoItems();

        var request = Request(
            harness.LibraryId,
            filterUserId: caller.Id.ToString("N"),
            includeNothing: true);
        var result = harness.Controller.PostIndex(request);

        Assert.Null(result.Result);
        Assert.NotNull(result.Value);
    }

    // ---- Server API keys: an administrator with no user of its own -----------------------

    [Fact]
    public void PostIndex_ApiKeyWithoutFilterUser_RunsWithNoUser()
    {
        // The Scheduler's sweep body: watched "all" and no userId.
        var harness = Harness.EmptyUserId(isApiKey: "True");
        // No user to check visibility against: a key reads any library, like an admin.
        harness.LibraryReturns(new StubLibrary(visible: false));
        harness.IndexReturnsNoItems();

        var request = Request(harness.LibraryId, includeNothing: true);
        request.Filters = new IndexFilters { Watched = "all" };
        var result = harness.Controller.PostIndex(request);

        Assert.Null(result.Result);
        Assert.NotNull(result.Value);
        harness.AssertIndexRanAs(user: null, isPlayed: null);
    }

    [Theory]
    [InlineData("unwatched", false)]
    [InlineData("all", null)] // no watched filter: still that user's view, not the key's
    public void PostIndex_ApiKeyWithFilterUser_RunsAsThatUser(string watched, bool? isPlayed)
    {
        var viewer = MakeUser(admin: false);
        var harness = Harness.EmptyUserId(isApiKey: "True", viewer);
        harness.LibraryReturns(new StubLibrary(visible: true));
        harness.IndexReturnsNoItems();

        var request = Request(harness.LibraryId, filterUserId: viewer.Id.ToString("N"), includeNothing: true);
        request.Filters!.Watched = watched;
        var result = harness.Controller.PostIndex(request);

        Assert.Null(result.Result);
        Assert.NotNull(result.Value);
        harness.AssertIndexRanAs(viewer, isPlayed);
    }

    [Theory]
    [InlineData("5f0c7a5e2b8d4c1f9e3a6b7c8d9e0f1a")] // well formed, no such user
    [InlineData("00000000000000000000000000000000")] // the empty id GetUserById throws on
    public void PostIndex_ApiKeyWithUnknownFilterUser_Returns400WithMessage(string filterUserId)
    {
        var harness = Harness.EmptyUserId(isApiKey: "True");
        harness.LibraryReturns(new StubLibrary(visible: true));

        var result = harness.Controller.PostIndex(Request(harness.LibraryId, filterUserId: filterUserId));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains(filterUserId, Assert.IsType<string>(badRequest.Value));
        harness.AssertBuilderNeverRan();
    }

    [Fact]
    public void PostIndex_EmptyUserIdWithoutApiKeyClaim_IsServedAsAnApiKey()
    {
        // Jellyfin writes the empty id only for a key, so it stands in when the claim is missing.
        var harness = Harness.EmptyUserId(isApiKey: null);
        harness.LibraryReturns(new StubLibrary(visible: true));
        harness.IndexReturnsNoItems();

        var result = harness.Controller.PostIndex(Request(harness.LibraryId, includeNothing: true));

        Assert.Null(result.Result);
        Assert.NotNull(result.Value);
        harness.AssertIndexRanAs(user: null, isPlayed: null);
    }

    [Fact]
    public void PostIndex_EmptyUserIdWithApiKeyClaimFalse_Returns403()
    {
        // The claim wins over the empty id: not a key, and no user, so denied.
        var harness = Harness.EmptyUserId(isApiKey: "False");
        harness.LibraryReturns(new StubLibrary(visible: true));

        var result = harness.Controller.PostIndex(Request(harness.LibraryId));

        Assert.IsType<ForbidResult>(result.Result);
        harness.AssertBuilderNeverRan();
    }

    // ---- Input validation gates (400) ---------------------------------------------------

    [Fact]
    public void PostIndex_NullBody_Returns400()
    {
        var harness = new Harness(MakeUser(admin: false));
        var result = harness.Controller.PostIndex(null!);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void PostIndex_ContractVersionBelowMinimum_Returns400()
    {
        var harness = new Harness(MakeUser(admin: false));
        var request = Request(harness.LibraryId);
        request.ContractVersion = CoaxContract.MinSupportedVersion - 1;

        var result = harness.Controller.PostIndex(request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void PostIndex_NoLibraryIds_Returns400()
    {
        var harness = new Harness(MakeUser(admin: false));
        var request = Request(harness.LibraryId);
        request.LibraryIds = Array.Empty<string>();

        var result = harness.Controller.PostIndex(request);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public void PostIndex_MalformedFilterUserId_Returns400()
    {
        var caller = MakeUser(admin: false);
        var harness = new Harness(caller);

        var request = Request(harness.LibraryId, filterUserId: "not-a-guid");
        var result = harness.Controller.PostIndex(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        harness.AssertBuilderNeverRan();
    }

    [Fact]
    public void PostIndex_UnknownLibrary_Returns400()
    {
        var caller = MakeUser(admin: false);
        var harness = new Harness(caller);
        harness.LibraryReturns(null);

        var result = harness.Controller.PostIndex(Request(harness.LibraryId));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        harness.AssertBuilderNeverRan();
    }

    // ---- Fixtures -----------------------------------------------------------------------

    private static IndexRequest Request(string libraryId, string? filterUserId = null, bool includeNothing = false)
    {
        return new IndexRequest
        {
            ContractVersion = CoaxContract.Version,
            LibraryIds = new[] { libraryId },
            Include = includeNothing ? Array.Empty<string>() : new[] { "items", "people" },
            Filters = filterUserId is null
                ? null
                : new IndexFilters { UserId = filterUserId, Watched = "all" }
        };
    }

    private static User MakeUser(bool admin)
    {
        var user = new User("tester", "AuthProvider", "ResetProvider")
        {
            Id = Guid.NewGuid()
        };
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        return user;
    }

    /// <summary>A library folder with a controllable visibility verdict (IsVisibleStandalone is virtual).</summary>
    private sealed class StubLibrary : Folder
    {
        private readonly bool _visible;

        public StubLibrary(bool visible) => _visible = visible;

        public override bool IsVisibleStandalone(User user) => _visible;
    }

    /// <summary>
    /// Wires a controller over a shared <see cref="ILibraryManager"/> mock (used by both the
    /// controller's access checks and the builder's gather step) so a single fixture drives
    /// the whole call path.
    /// </summary>
    private sealed class Harness
    {
        private readonly Mock<ILibraryManager> _library = new();
        private readonly Mock<IUserManager> _users = new();
        private bool _indexConfigured;

        public Harness(User? caller)
        {
            // Jellyfin's UserManager.GetUserById throws on the empty id (10.11 and 12); so does the fake.
            _users.Setup(m => m.GetUserById(Guid.Empty))
                .Throws(new ArgumentException("Guid can't be empty", "id"));
            if (caller is not null)
            {
                _users.Setup(m => m.GetUserById(caller.Id)).Returns(caller);
            }

            var builder = new CoaxIndexBuilder(
                _library.Object,
                _users.Object,
                new Mock<ILocalizationManager>().Object,
                new Mock<IDbContextFactory<JellyfinDbContext>>().Object,
                NullLogger<CoaxIndexBuilder>.Instance);

            Controller = new CoaxController(
                _library.Object,
                _users.Object,
                builder,
                NullLogger<CoaxController>.Instance);

            var http = new DefaultHttpContext();
            var identity = caller is null
                ? new ClaimsIdentity()
                : new ClaimsIdentity(new[] { new Claim(UserIdClaim, caller.Id.ToString("N")) }, "test");
            http.User = new ClaimsPrincipal(identity);
            Controller.ControllerContext = new ControllerContext { HttpContext = http };
        }

        public CoaxController Controller { get; }

        public string LibraryId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// A harness whose principal carries the empty GUID as its user id, the way Jellyfin's
        /// CustomAuthenticationHandler writes a server API key, with <paramref name="isApiKey"/>
        /// as the IsApiKey claim (left off when null). <paramref name="knownUsers"/> resolve by id.
        /// </summary>
        public static Harness EmptyUserId(string? isApiKey, params User[] knownUsers)
        {
            var harness = new Harness(caller: null);
            var claims = new List<Claim> { new(UserIdClaim, Guid.Empty.ToString("N")) };
            if (isApiKey is not null)
            {
                claims.Add(new Claim(IsApiKeyClaim, isApiKey));
            }

            harness.Controller.ControllerContext.HttpContext.User =
                new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            foreach (var user in knownUsers)
            {
                harness._users.Setup(m => m.GetUserById(user.Id)).Returns(user);
            }

            return harness;
        }

        public void LibraryReturns(BaseItem? library)
            => _library.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns(library);

        public void IndexReturnsNoItems()
        {
            _indexConfigured = true;
            _library
                .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem>());
        }

        /// <summary>The builder's only observable side effect is the gather query; assert it never fired.</summary>
        public void AssertBuilderNeverRan()
        {
            Assert.False(_indexConfigured, "Test misconfigured: do not stub the index query on a denial path.");
            _library.Verify(m => m.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        }

        /// <summary>
        /// Asserts the gather query ran once over the requested library, as <paramref name="user"/>
        /// (null: no user, so no per-user restriction) with the given played filter.
        /// </summary>
        public void AssertIndexRanAs(User? user, bool? isPlayed)
        {
            var libraryId = Guid.Parse(LibraryId);
            _library.Verify(
                m => m.GetItemList(It.Is<InternalItemsQuery>(
                    q => q.User == user && q.IsPlayed == isPlayed && q.ParentId == libraryId)),
                Times.Once);
        }
    }
}

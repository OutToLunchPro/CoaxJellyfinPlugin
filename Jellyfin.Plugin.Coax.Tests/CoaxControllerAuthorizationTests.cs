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
        private bool _indexConfigured;

        public Harness(User? caller)
        {
            var users = new Mock<IUserManager>();
            if (caller is not null)
            {
                users.Setup(m => m.GetUserById(caller.Id)).Returns(caller);
            }

            var builder = new CoaxIndexBuilder(
                _library.Object,
                users.Object,
                new Mock<ILocalizationManager>().Object,
                new Mock<IDbContextFactory<JellyfinDbContext>>().Object,
                NullLogger<CoaxIndexBuilder>.Instance);

            Controller = new CoaxController(
                _library.Object,
                users.Object,
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
    }
}

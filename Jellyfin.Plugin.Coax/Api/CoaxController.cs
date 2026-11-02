using System;
using System.Globalization;
using System.Net.Mime;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Coax.Indexing;
using Jellyfin.Plugin.Coax.Models;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Coax.Api;

/// <summary>
/// Coax data endpoints: a capability probe and the stateless person/collection index.
/// </summary>
[ApiController]
[Authorize]
[Route("coax")]
[Produces(MediaTypeNames.Application.Json)]
public class CoaxController : ControllerBase
{
    /// <summary>
    /// Claim the Jellyfin auth pipeline stamps onto the request principal, holding the
    /// authenticated user id as an "N"-formatted GUID. The constant itself
    /// (<c>Jellyfin.Api.Constants.InternalClaimTypes.UserId</c>) lives in the Jellyfin.Api
    /// assembly, which plugins don't reference, so we match the literal. Stable across
    /// Jellyfin 10.8–12.4 — worth re-checking on a major server bump.
    /// </summary>
    private const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>
    /// Claim the same pipeline stamps <c>True</c> when the token is a server API key
    /// (<c>Jellyfin.Api.Constants.InternalClaimTypes.IsApiKey</c>). A key has no user of its
    /// own: its <see cref="UserIdClaim"/> is the empty GUID.
    /// </summary>
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly CoaxIndexBuilder _indexBuilder;
    private readonly ILogger<CoaxController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoaxController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="indexBuilder">The shared, DI-scoped index builder.</param>
    /// <param name="logger">The logger.</param>
    public CoaxController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        CoaxIndexBuilder indexBuilder,
        ILogger<CoaxController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _indexBuilder = indexBuilder;
        _logger = logger;
    }

    /// <summary>
    /// Capability probe. Lets the client feature-gate on contract version + capabilities.
    /// </summary>
    /// <response code="200">Capability descriptor returned.</response>
    /// <returns>The plugin capability descriptor.</returns>
    [HttpGet("info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<InfoResponse> GetInfo()
    {
        var version = Plugin.Instance?.Version.ToString() ?? "1.0.0";
        return new InfoResponse { PluginVersion = version };
    }

    /// <summary>
    /// Builds the person→items inverse, collection memberships, and item metadata for the
    /// requested libraries. Stateless — recomputed from the library on every call. Every
    /// access is scoped to the authenticated caller; a non-admin may only read their own
    /// watch state and only from libraries they can see. A server API key reads as an
    /// administrator with no user of its own.
    /// </summary>
    /// <param name="request">The index request.</param>
    /// <response code="200">Index built.</response>
    /// <response code="400">The request was malformed or unsupported.</response>
    /// <response code="403">The caller may not read the requested user or library.</response>
    /// <returns>The computed index.</returns>
    [HttpPost("index")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IndexResponse> PostIndex([FromBody] IndexRequest request)
    {
        if (request is null)
        {
            return BadRequest("Missing request body.");
        }

        // Backward-compatible contract gate: accept anything this server still understands
        // rather than an exact match, so additive minor bumps don't break older clients.
        if (request.ContractVersion < CoaxContract.MinSupportedVersion)
        {
            return BadRequest(
                $"Unsupported contractVersion {request.ContractVersion}; this plugin supports "
                + $"{CoaxContract.MinSupportedVersion}..{CoaxContract.Version}.");
        }

        if (request.LibraryIds.Count == 0)
        {
            return BadRequest("At least one libraryId is required.");
        }

        // --- AuthN: map the session principal to a real user or a server API key. Fail closed. ---
        var caller = ResolveCaller(out var isApiKey);
        if (caller is null && !isApiKey)
        {
            // [Authorize] was satisfied but the principal is neither a user nor a server API key.
            _logger.LogWarning("Coax: authenticated request without a resolvable user; denied.");
            return Forbid();
        }

        // Jellyfin treats a server API key as an administrator (it satisfies RequiresElevation).
        // HasPermission is an extension on IHasPermissions (Jellyfin.Data.UserEntityExtensions),
        // which the User entity implements.
        var isAdmin = isApiKey || caller!.HasPermission(PermissionKind.IsAdministrator);

        // --- AuthZ #1: watch-history scope. A non-admin may only read their own history. ---
        // The index runs as the caller; a key has no user, so it runs as filters.userId if given.
        var accessUser = caller;
        var scopeError = isApiKey
            ? ResolveApiKeyUser(request, out accessUser)
            : AuthorizeUserScope(request, caller!, isAdmin);
        if (scopeError is not null)
        {
            return scopeError;
        }

        // --- AuthZ #2: library scope. A non-admin may only draw from visible libraries. ---
        var libraryError = AuthorizeLibraryScope(request, caller, isAdmin);
        if (libraryError is not null)
        {
            return libraryError;
        }

        try
        {
            // Pass the caller so the underlying query runs as that user: Jellyfin then
            // enforces their library access + parental/tag rules instead of an unrestricted
            // (null-user) query that would leak across the whole server. Only a server API
            // key without filters.userId runs with no user, as Jellyfin's own /Items does.
            return _indexBuilder.Build(request, accessUser);
        }
        catch (ArgumentException ex)
        {
            // Bad GUID, missing userId for a watched filter, etc.
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Enforces that the watch-history target (<c>filters.userId</c>) is the caller, unless
    /// the caller is an administrator. When no user is supplied, binds any watched filter to
    /// the caller so it can never run against a null/ambient user.
    /// </summary>
    private ActionResult? AuthorizeUserScope(IndexRequest request, User caller, bool isAdmin)
    {
        var filters = request.Filters;
        if (filters is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(filters.UserId))
        {
            // Default the watched scope to the caller; never leave it unset (=> null user).
            filters.UserId = caller.Id.ToString("N", CultureInfo.InvariantCulture);
            return null;
        }

        if (!Guid.TryParse(filters.UserId, out var requestedUserId))
        {
            return BadRequest($"filters.userId is not a valid id: {filters.UserId}");
        }

        if (requestedUserId != caller.Id && !isAdmin)
        {
            _logger.LogWarning(
                "Coax: user {Caller} attempted to read user {Target}'s watch data; denied.",
                caller.Id,
                requestedUserId);
            return Forbid();
        }

        return null;
    }

    /// <summary>
    /// Picks the user a server API key's index runs as. A key has no user of its own, so the
    /// query runs as <c>filters.userId</c> when one is given (their watch state and parental
    /// rules) and with no user when not: the administrator's view Jellyfin gives a key.
    /// </summary>
    private ActionResult? ResolveApiKeyUser(IndexRequest request, out User? accessUser)
    {
        accessUser = null;
        var userId = request.Filters?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        if (!Guid.TryParse(userId, out var id))
        {
            return BadRequest($"filters.userId is not a valid id: {userId}");
        }

        // GetUserById throws on the empty id, which names no user anyway.
        accessUser = id == Guid.Empty ? null : _userManager.GetUserById(id);
        return accessUser is null ? BadRequest($"No such user: {userId}") : null;
    }

    /// <summary>
    /// Enforces that every requested library exists and — for non-admins — is one the caller
    /// can actually see. Closes the "pass arbitrary libraryIds to scrape a restricted
    /// library" vector.
    /// </summary>
    private ActionResult? AuthorizeLibraryScope(IndexRequest request, User? caller, bool isAdmin)
    {
        foreach (var libraryId in request.LibraryIds)
        {
            if (!Guid.TryParse(libraryId, out var libGuid))
            {
                return BadRequest($"Invalid libraryId: {libraryId}");
            }

            var library = _libraryManager.GetItemById(libGuid);
            if (library is null)
            {
                return BadRequest($"No such library: {libraryId}");
            }

            if (!isAdmin && (caller is null || !library.IsVisibleStandalone(caller)))
            {
                _logger.LogWarning(
                    "Coax: user {Caller} requested inaccessible library {Library}; denied.",
                    caller?.Id,
                    libGuid);
                return Forbid();
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the authenticated user from the request principal. Returns <c>null</c> when
    /// the principal carries no resolvable user, so callers can fail closed. Never throws: an
    /// empty, unparseable or unknown id is no user. <paramref name="isApiKey"/> marks a server
    /// API key, which has no user: from the IsApiKey claim, else from the empty id Jellyfin
    /// writes for a key.
    /// </summary>
    private User? ResolveCaller(out bool isApiKey)
    {
        var raw = User.FindFirst(UserIdClaim)?.Value;
        var hasId = Guid.TryParse(raw, out var id);
        isApiKey = bool.TryParse(User.FindFirst(IsApiKeyClaim)?.Value, out var claimed)
            ? claimed
            : hasId && id == Guid.Empty;
        if (isApiKey)
        {
            return null;
        }

        // GetUserById throws on the empty id, so only a real id is looked up.
        if (hasId
            && id != Guid.Empty
            && _userManager.GetUserById(id) is { } user)
        {
            return user;
        }

        // Fallback for hosting paths that surface the user via HttpContext.Items instead.
        if (HttpContext.Items.TryGetValue("User", out var item) && item is User contextUser)
        {
            return contextUser;
        }

        return null;
    }
}

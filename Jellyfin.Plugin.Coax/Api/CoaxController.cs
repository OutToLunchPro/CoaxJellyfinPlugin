using System;
using System.Net.Mime;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Coax.Indexing;
using Jellyfin.Plugin.Coax.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
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
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILocalizationManager _localization;
    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory;
    private readonly ILogger<CoaxController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoaxController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="localization">The localization manager (for content-rating levels).</param>
    /// <param name="dbFactory">Factory for the Jellyfin database context (for the people join).</param>
    /// <param name="logger">The logger.</param>
    public CoaxController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILocalizationManager localization,
        IDbContextFactory<JellyfinDbContext> dbFactory,
        ILogger<CoaxController> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _localization = localization;
        _dbFactory = dbFactory;
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
    /// requested libraries. Stateless — recomputed from the library on every call.
    /// </summary>
    /// <param name="request">The index request.</param>
    /// <response code="200">Index built.</response>
    /// <response code="400">The request was malformed or unsupported.</response>
    /// <returns>The computed index.</returns>
    [HttpPost("index")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IndexResponse> PostIndex([FromBody] IndexRequest request)
    {
        if (request is null)
        {
            return BadRequest("Missing request body.");
        }

        if (request.ContractVersion != CoaxContract.Version)
        {
            return BadRequest($"Unsupported contractVersion {request.ContractVersion}; this plugin speaks {CoaxContract.Version}.");
        }

        if (request.LibraryIds.Count == 0)
        {
            return BadRequest("At least one libraryId is required.");
        }

        try
        {
            var builder = new CoaxIndexBuilder(_libraryManager, _userManager, _localization, _dbFactory, _logger);
            return builder.Build(request);
        }
        catch (ArgumentException ex)
        {
            // Bad GUID, missing userId for a watched filter, etc.
            return BadRequest(ex.Message);
        }
    }
}

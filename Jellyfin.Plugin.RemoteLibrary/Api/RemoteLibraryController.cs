using Jellyfin.Plugin.RemoteLibrary.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.RemoteLibrary.Api;

[ApiController]
[Route("RemoteLibrary")]
public sealed class RemoteLibraryController : ControllerBase
{
    private readonly RemoteLibraryService _service;
    private readonly IUserManager _userManager;

    public RemoteLibraryController(RemoteLibraryService service, IUserManager userManager)
    {
        _service = service;
        _userManager = userManager;
    }

    [HttpGet("LocalReviews/{mediaType}/{tmdbId}")]
    [Authorize]
    public IActionResult LocalReviews(string mediaType, string tmdbId)
    {
        if (mediaType is not ("movie" or "tv")) return BadRequest();
        return Ok(new { reviews = ResolveReviewAuthors(_service.GetLocalReviews(mediaType, tmdbId)) });
    }

    [HttpGet("Reviews/Export")]
    [Authorize]
    public IActionResult ExportReviews([FromQuery] int limit = 500, [FromQuery] int offset = 0)
    {
        if (limit is < 1 or > 1000 || offset < 0) return BadRequest();
        var all = ResolveReviewAuthors(_service.GetExportableReviews());
        return Ok(new { reviews = all.Skip(offset).Take(limit), total = all.Count, offset, limit });
    }

    [HttpPost("Reviews/Sync")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<ReviewSyncResult>> SyncReviews(CancellationToken cancellationToken)
        => Ok(await _service.SyncReviewsAsync(cancellationToken).ConfigureAwait(false));

    [HttpGet("ManagedMedia")]
    [Authorize]
    public ActionResult<ManagedMediaManifest> ManagedMedia()
        => Ok(_service.GetManagedMediaManifest());

    private IReadOnlyList<RemoteLibraryService.RemoteReview> ResolveReviewAuthors(
        IReadOnlyList<RemoteLibraryService.RemoteReview> reviews)
    {
        foreach (var review in reviews)
        {
            if (review.Imported || !Guid.TryParseExact(review.UserId, "N", out var userId)) continue;
            var user = _userManager.GetUserById(userId);
            if (user is not null) review.UserName = user.Username;
        }

        return reviews;
    }

    [HttpGet("Integration.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public IActionResult IntegrationScript()
    {
        var resource = typeof(RemoteLibraryController).Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.RemoteLibrary.Web.integration.js");
        return resource is null ? NotFound() : File(resource, "text/javascript; charset=utf-8");
    }

    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<RemoteStatus>> Status([FromQuery] string serverId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.TestAsync(serverId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return BadRequest(new RemoteStatus(false, null, null, exception.Message));
        }
    }

    [HttpPost("QuickConnect/Initiate")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<QuickConnectStart>> InitiateQuickConnect([FromQuery] string serverId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.InitiateQuickConnectAsync(serverId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("QuickConnect/Complete")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<RemoteStatus>> CompleteQuickConnect([FromQuery] string serverId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.CompleteQuickConnectAsync(serverId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost("Sync")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<SyncJobStatus> Sync()
    {
        try
        {
            return Accepted(_service.StartSync());
        }
        catch (Exception exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpGet("Sync/Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<SyncJobStatus> SyncStatus()
        => Ok(_service.GetSyncStatus());

    [HttpGet("Reviews/{mediaType}/{tmdbId}")]
    [Authorize]
    public async Task<ActionResult<object>> Reviews(string mediaType, string tmdbId, CancellationToken cancellationToken)
        => Ok(new { reviews = await _service.GetReviewsAsync(mediaType, tmdbId, cancellationToken).ConfigureAwait(false) });

    [HttpGet("Reachability")]
    [Authorize]
    public async Task<ActionResult<RemoteReachabilitySummary>> Reachability(CancellationToken cancellationToken)
        => Ok(await _service.GetReachabilityAsync(cancellationToken).ConfigureAwait(false));

    [HttpGet("Stream/{serverId}/{itemId}")]
    [HttpHead("Stream/{serverId}/{itemId}")]
    [AllowAnonymous]
    public async Task StreamFromServer(string serverId, string itemId, [FromQuery] string secret, CancellationToken cancellationToken)
    {
        if (!_service.ValidateStreamSecret(secret))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _service.ProxyAsync(serverId, itemId, image: false, Request, Response, cancellationToken).ConfigureAwait(false);
    }

    [HttpGet("Image/{serverId}/{itemId}")]
    [AllowAnonymous]
    public async Task ImageFromServer(string serverId, string itemId, [FromQuery] string secret, CancellationToken cancellationToken)
    {
        if (!_service.ValidateStreamSecret(secret))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _service.ProxyAsync(serverId, itemId, image: true, Request, Response, cancellationToken).ConfigureAwait(false);
    }

    [HttpGet("ReviewAvatar/{serverId}/{userId}/{secret}")]
    [AllowAnonymous]
    public IActionResult ReviewAvatar(string serverId, string userId, string secret)
    {
        if (!_service.ValidateStreamSecret(secret)) return Forbid();
        return _service.TryGetReviewAvatar(serverId, userId, out var path, out var contentType)
            ? PhysicalFile(path, contentType)
            : NotFound();
    }

    [HttpGet("Stream/{itemId}")]
    [HttpHead("Stream/{itemId}")]
    [AllowAnonymous]
    public async Task Stream(string itemId, [FromQuery] string secret, CancellationToken cancellationToken)
    {
        if (!_service.ValidateStreamSecret(secret))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _service.ProxyAsync(null, itemId, image: false, Request, Response, cancellationToken).ConfigureAwait(false);
    }

    [HttpGet("Image/{itemId}")]
    [AllowAnonymous]
    public async Task Image(string itemId, [FromQuery] string secret, CancellationToken cancellationToken)
    {
        if (!_service.ValidateStreamSecret(secret))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _service.ProxyAsync(null, itemId, image: true, Request, Response, cancellationToken).ConfigureAwait(false);
    }
}

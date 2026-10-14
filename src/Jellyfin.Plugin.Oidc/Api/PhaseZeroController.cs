using System.Security.Claims;
using Jellyfin.Plugin.Oidc.Authentication;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Oidc.Api;

/// <summary>
/// Admin-only endpoints for the temporary Phase 0 compatibility spike.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/Oidc/Phase0")]
public sealed class PhaseZeroController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly MockLoginTicketStore _ticketStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PhaseZeroController"/> class.
    /// </summary>
    public PhaseZeroController(
        IUserManager userManager,
        MockLoginTicketStore ticketStore)
    {
        _userManager = userManager;
        _ticketStore = ticketStore;
    }

    /// <summary>
    /// Issues a short-lived mock external-login result for an existing Jellyfin account.
    /// </summary>
    [HttpPost("mock-login/{userId:guid}")]
    public ActionResult<MockLoginTicketResponse> IssueMockLogin(Guid userId)
    {
        if (Plugin.Instance?.Configuration.EnablePhaseZeroMockLogin != true)
        {
            return NotFound();
        }

        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var user = _userManager.GetUserById(userId);
        if (user is null || string.IsNullOrWhiteSpace(user.Username))
        {
            return NotFound("The selected Jellyfin user does not exist.");
        }

        return Ok(new MockLoginTicketResponse(user.Username, _ticketStore.Issue(user.Id)));
    }

    /// <summary>
    /// Describes the authenticated account for the local Phase 0 Web UI check.
    /// </summary>
    [HttpGet("who-am-i")]
    public ActionResult<string> WhoAmI()
    {
        return Ok(User.FindFirstValue(ClaimTypes.Name) ?? string.Empty);
    }
}

/// <summary>
/// One-time mock login credential returned to an administrator during the spike.
/// </summary>
public sealed record MockLoginTicketResponse(string Username, string Credential);

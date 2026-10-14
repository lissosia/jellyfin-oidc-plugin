using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.Oidc.Authentication;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Oidc.Api;

/// <summary>Starts Pocket ID authorization and completes verified browser logins.</summary>
[ApiController]
[AllowAnonymous]
[Route("Plugins/Oidc/Oidc")]
public sealed class OidcController : ControllerBase
{
    private const string CookiePrefix = "jellyfin-oidc-state-";
    private const string CallbackPath = "/Plugins/Oidc/Oidc/callback";
    private readonly OidcService _oidcService;
    private readonly OidcTransactionStore _transactionStore;
    private readonly UserProvisioningService _provisioningService;
    private readonly MockLoginTicketStore _ticketStore;
    private readonly ILogger<OidcController> _logger;

    /// <summary>Initializes a new instance of the <see cref="OidcController"/> class.</summary>
    public OidcController(
        OidcService oidcService,
        OidcTransactionStore transactionStore,
        UserProvisioningService provisioningService,
        MockLoginTicketStore ticketStore,
        ILogger<OidcController> logger)
    {
        _oidcService = oidcService;
        _transactionStore = transactionStore;
        _provisioningService = provisioningService;
        _ticketStore = ticketStore;
        _logger = logger;
    }

    /// <summary>Redirects the browser to the configured identity provider.</summary>
    [HttpGet("login")]
    public async Task<IActionResult> Login(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.EnableOidc != true) return NotFound();

        try
        {
            var (redirect, transaction) = await _oidcService.CreateAuthorizationRequestAsync(configuration, cancellationToken).ConfigureAwait(false);
            Response.Cookies.Append(CookiePrefix + transaction.State, transaction.State, new CookieOptions
            {
                HttpOnly = true,
                Secure = transaction.RedirectUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                SameSite = SameSiteMode.Lax,
                Path = CallbackPath,
                IsEssential = true,
                Expires = transaction.ExpiresAt
            });
            return Redirect(redirect.AbsoluteUri);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or IOException)
        {
            _logger.LogWarning(exception, "Could not begin Pocket ID login.");
            return Content("<p>OIDC login could not start. Check the OpenID Connect plugin settings and Jellyfin server log.</p>", "text/html");
        }
    }

    /// <summary>Validates the OIDC response, applies configured account synchronization, and emits a one-use bridge credential.</summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? state, [FromQuery] string? code, [FromQuery] string? error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 128) return BadRequest("Invalid OIDC state.");
        var cookieName = CookiePrefix + state;
        var cookieState = Request.Cookies[cookieName];
        Response.Cookies.Delete(cookieName, new CookieOptions { Path = CallbackPath, Secure = Request.IsHttps, HttpOnly = true, SameSite = SameSiteMode.Lax });
        if (string.IsNullOrEmpty(cookieState)
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(cookieState))
            || !_transactionStore.TryConsume(state, out var transaction))
        {
            return BadRequest("This OIDC login expired or did not start in this browser. Start again from Jellyfin.");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return Content("<p>Pocket ID did not complete the sign-in. Return to Jellyfin and try again.</p>", "text/html");
        }

        try
        {
            var configuration = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("OIDC is not configured.");
            if (configuration.EnableOidc != true) return NotFound();
            var identity = await _oidcService.RedeemAuthorizationCodeAsync(configuration, transaction, code ?? string.Empty, cancellationToken).ConfigureAwait(false);
            var user = await _provisioningService.ResolveAndSyncAsync(identity, cancellationToken).ConfigureAwait(false);
            var credential = _ticketStore.Issue(user.Id);
            var origin = new Uri(transaction.RedirectUri).GetLeftPart(UriPartial.Authority);
            var payload = JsonSerializer.Serialize(new { type = "jellyfin-oidc-login", username = user.Username, credential });
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            Response.Headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
            Response.Headers.CacheControl = "no-store";
            return Content($"<!doctype html><meta charset=\"utf-8\"><title>Jellyfin sign-in</title><p id=\"result\">Pocket ID verified. Finishing Jellyfin sign-in…</p><script nonce=\"{nonce}\">if(window.opener){{window.opener.postMessage({payload},{JsonSerializer.Serialize(origin)});document.getElementById('result').textContent='Sign-in verified. Return to the Jellyfin tab.';}}else{{document.getElementById('result').textContent='Sign-in verified. Return to Jellyfin to finish.';}}</script>", "text/html");
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or IOException or SecurityTokenException or ArgumentException)
        {
            _logger.LogWarning(exception, "Pocket ID login failed validation or account synchronization.");
            return Content("<!doctype html><meta charset=\"utf-8\"><title>Jellyfin sign-in failed</title><p>Jellyfin could not complete this Pocket ID sign-in. Return to Jellyfin and check the plugin settings.</p>", "text/html");
        }
    }
}

/// <summary>Administrator-only configuration endpoints for Pocket ID.</summary>
[ApiController]
[Authorize]
[Route("Plugins/Oidc/Settings")]
public sealed class OidcSettingsController : ControllerBase
{
    /// <summary>Gets the editable OIDC settings without returning the stored secret.</summary>
    [HttpGet]
    public ActionResult<OidcSettingsDto> GetSettings()
    {
        if (!User.IsInRole("Administrator")) return Forbid();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(OidcSettingsDto.From(config, !string.IsNullOrEmpty(config.ProtectedClientSecret)));
    }

    /// <summary>Saves OIDC settings and protects a newly entered client secret.</summary>
    [HttpPut]
    public ActionResult<OidcSettingsDto> SaveSettings([FromBody] OidcSettingsRequest request)
    {
        if (!User.IsInRole("Administrator")) return Forbid();
        var plugin = Plugin.Instance;
        if (plugin is null) return StatusCode(503);
        if (request.EnableOidc && (string.IsNullOrWhiteSpace(request.Issuer) || string.IsNullOrWhiteSpace(request.ClientId)))
            return BadRequest("Enter an issuer URL and client ID before enabling OIDC.");
        if (request.EnableUserProvisioning && string.IsNullOrWhiteSpace(request.AllowedGroup))
            return BadRequest("Set the group required for account provisioning.");
        if (request.EnableAdministratorGroupMapping && string.IsNullOrWhiteSpace(request.AdministratorGroup))
            return BadRequest("Set an administrator group before enabling that mapping.");
        if (request.AvatarSyncMode is not ("Disabled" or "ImportOnce" or "AlwaysUpdate"))
            return BadRequest("Avatar sync mode must be Disabled, ImportOnce, or AlwaysUpdate.");

        var issuer = request.Issuer?.Trim().TrimEnd('/') ?? string.Empty;
        var publicBaseUrl = request.PublicBaseUrl?.Trim() ?? string.Empty;
        var configuration = plugin.Configuration;
        var candidate = new PluginConfiguration
        {
            EnableOidc = request.EnableOidc,
            Issuer = issuer,
            PublicBaseUrl = publicBaseUrl,
            ClientId = request.ClientId?.Trim() ?? string.Empty,
            ProtectedClientSecret = configuration.ProtectedClientSecret,
            AllowedGroup = request.AllowedGroup?.Trim() ?? string.Empty,
            AdministratorGroup = request.AdministratorGroup?.Trim() ?? string.Empty,
            EnableUserProvisioning = request.EnableUserProvisioning,
            EnableAdministratorGroupMapping = request.EnableAdministratorGroupMapping,
            AvatarSyncMode = request.AvatarSyncMode
        };
        try
        {
            if (request.EnableOidc) _ = Jellyfin.Plugin.Oidc.Authentication.OidcService.GetCallbackUri(candidate);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }

        configuration.EnableOidc = candidate.EnableOidc;
        configuration.Issuer = candidate.Issuer;
        configuration.PublicBaseUrl = candidate.PublicBaseUrl;
        configuration.ClientId = candidate.ClientId;
        configuration.AllowedGroup = candidate.AllowedGroup;
        configuration.AdministratorGroup = candidate.AdministratorGroup;
        configuration.EnableUserProvisioning = candidate.EnableUserProvisioning;
        configuration.EnableAdministratorGroupMapping = candidate.EnableAdministratorGroupMapping;
        configuration.AvatarSyncMode = candidate.AvatarSyncMode;
        if (!string.IsNullOrEmpty(request.ClientSecret)) plugin.SetClientSecret(request.ClientSecret);
        plugin.UpdateConfiguration(configuration);

        return Ok(OidcSettingsDto.From(configuration, !string.IsNullOrEmpty(configuration.ProtectedClientSecret)));
    }
}

/// <summary>Editable OIDC configuration. Secret is only accepted on writes.</summary>
public sealed record OidcSettingsRequest(
    bool EnableOidc,
    string? Issuer,
    string? PublicBaseUrl,
    string? ClientId,
    string? ClientSecret,
    string? AllowedGroup,
    string? AdministratorGroup,
    bool EnableUserProvisioning,
    bool EnableAdministratorGroupMapping,
    string AvatarSyncMode);

/// <summary>OIDC configuration returned to the Jellyfin settings page.</summary>
public sealed record OidcSettingsDto(
    bool EnableOidc,
    string Issuer,
    string PublicBaseUrl,
    string ClientId,
    bool ClientSecretConfigured,
    string AllowedGroup,
    string AdministratorGroup,
    bool EnableUserProvisioning,
    bool EnableAdministratorGroupMapping,
    string AvatarSyncMode,
    string CallbackUri)
{
    internal static OidcSettingsDto From(PluginConfiguration configuration, bool secretConfigured)
    {
        string callback;
        try { callback = Jellyfin.Plugin.Oidc.Authentication.OidcService.GetCallbackUri(configuration); }
        catch (InvalidOperationException) { callback = "Set a valid public Jellyfin HTTPS URL to generate the redirect URI."; }
        return new OidcSettingsDto(configuration.EnableOidc, configuration.Issuer, configuration.PublicBaseUrl,
            configuration.ClientId, secretConfigured, configuration.AllowedGroup, configuration.AdministratorGroup,
            configuration.EnableUserProvisioning, configuration.EnableAdministratorGroupMapping, configuration.AvatarSyncMode, callback);
    }
}

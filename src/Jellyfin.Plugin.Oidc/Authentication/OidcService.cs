using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Oidc.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.Oidc.Authentication;

/// <summary>Implements the OIDC authorization-code flow with PKCE and signed ID-token validation.</summary>
public sealed class OidcService
{
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Managers = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OidcTransactionStore _transactionStore;

    /// <summary>Initializes a new instance of the <see cref="OidcService"/> class.</summary>
    public OidcService(IHttpClientFactory httpClientFactory, OidcTransactionStore transactionStore)
    {
        _httpClientFactory = httpClientFactory;
        _transactionStore = transactionStore;
    }

    /// <summary>Gets the validated callback URI from the configured public Jellyfin URL.</summary>
    public static string GetCallbackUri(PluginConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration.PublicBaseUrl, UriKind.Absolute, out var baseUri)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The public Jellyfin URL must use HTTPS for OpenID Connect.");
        }

        var basePath = baseUri.AbsoluteUri.EndsWith('/') ? baseUri.AbsoluteUri : baseUri.AbsoluteUri + "/";
        return new Uri(new Uri(basePath), "Plugins/Oidc/Oidc/callback").AbsoluteUri;
    }

    /// <summary>Builds an authorization redirect and stores its state, nonce, and PKCE verifier.</summary>
    public async Task<(Uri RedirectUri, OidcTransaction Transaction)> CreateAuthorizationRequestAsync(
        OidcProviderConfiguration configuration,
        string providerId,
        PluginConfiguration serverConfiguration,
        CancellationToken cancellationToken)
    {
        var clientId = Require(configuration.ClientId, "Client ID");
        var issuer = GetIssuer(configuration.Issuer);
        var metadata = await GetConfigurationAsync(issuer, cancellationToken).ConfigureAwait(false);
        ValidateMetadataIssuer(metadata, issuer);
        var authorizationUri = ValidateEndpoint(metadata.AuthorizationEndpoint, issuer, "authorization");
        _ = ValidateEndpoint(metadata.JwksUri, issuer, "signing keys");

        var transaction = _transactionStore.Create(providerId, GetCallbackUri(serverConfiguration));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(transaction.CodeVerifier)));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = transaction.RedirectUri,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["scope"] = EnsureOpenIdScope(configuration.Scopes),
            ["state"] = transaction.State,
            ["nonce"] = transaction.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return (new Uri(QueryHelpers.AddQueryString(metadata.AuthorizationEndpoint, query)), transaction);
    }

    /// <summary>Consumes an authorization code, validates the ID token, and returns the verified identity.</summary>
    public async Task<VerifiedOidcIdentity> RedeemAuthorizationCodeAsync(
        OidcProviderConfiguration configuration,
        OidcTransaction transaction,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 4096)
        {
            throw new InvalidOperationException("The identity provider returned an invalid authorization code.");
        }

        var issuer = GetIssuer(configuration.Issuer);
        var metadata = await GetConfigurationAsync(issuer, cancellationToken).ConfigureAwait(false);
        ValidateMetadataIssuer(metadata, issuer);
        var tokenUri = ValidateEndpoint(metadata.TokenEndpoint, issuer, "token");
        _ = ValidateEndpoint(metadata.JwksUri, issuer, "signing keys");

        using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = transaction.RedirectUri,
            ["client_id"] = Require(configuration.ClientId, "Client ID"),
            ["code_verifier"] = transaction.CodeVerifier
        });
        var clientSecret = Plugin.Instance?.GetClientSecret(configuration.ProtectedClientSecret) ?? string.Empty;
        if (!string.IsNullOrEmpty(clientSecret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(Uri.EscapeDataString(Require(configuration.ClientId, "Client ID")) + ":" + Uri.EscapeDataString(clientSecret))));
        }

        var client = _httpClientFactory.CreateClient("OidcProtocol");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The identity provider rejected the authorization code.");
        }

        var tokenBytes = await ReadBoundedAsync(response.Content, 64 * 1024, cancellationToken).ConfigureAwait(false);
        var tokenResponse = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(tokenBytes, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The identity provider returned an empty token response.");
        if (string.IsNullOrWhiteSpace(tokenResponse.IdToken) || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("The identity provider did not return the required ID and access tokens.");
        }

        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = metadata.Issuer,
            ValidateAudience = true,
            ValidAudience = Require(configuration.ClientId, "Client ID"),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512],
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "preferred_username",
            RoleClaimType = "groups"
        };
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(tokenResponse.IdToken, validation, out var validatedToken);
        if (validatedToken is not JwtSecurityToken)
        {
            throw new InvalidOperationException("The identity provider returned an unsupported ID token.");
        }

        var nonce = principal.FindFirst("nonce")?.Value;
        if (string.IsNullOrEmpty(nonce) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(transaction.Nonce)))
        {
            throw new InvalidOperationException("The ID token nonce did not match this login request.");
        }

        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512)
        {
            throw new InvalidOperationException("The verified identity has no valid subject.");
        }

        return VerifiedOidcIdentity.Create(configuration.Id, configuration.UsernameClaim, configuration.GroupsClaim, metadata.Issuer, subject, principal);
    }

    private async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string issuer, CancellationToken cancellationToken)
    {
        var discoveryUri = new Uri(issuer.TrimEnd('/') + "/.well-known/openid-configuration").AbsoluteUri;
        var manager = Managers.GetOrAdd(discoveryUri, url => new ConfigurationManager<OpenIdConnectConfiguration>(
            url,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_httpClientFactory.CreateClient("OidcProtocol")) { RequireHttps = true }));
        return await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetIssuer(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var issuer)
            || issuer.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(issuer.UserInfo)
            || !string.IsNullOrEmpty(issuer.Query)
            || !string.IsNullOrEmpty(issuer.Fragment))
        {
            throw new InvalidOperationException("Set the identity provider's HTTPS issuer URL.");
        }

        var issuerPath = issuer.GetLeftPart(UriPartial.Path);
        const string discoverySuffix = "/.well-known/openid-configuration";
        if (issuerPath.EndsWith(discoverySuffix, StringComparison.OrdinalIgnoreCase))
        {
            issuerPath = issuerPath[..^discoverySuffix.Length];
        }

        // Uri.GetLeftPart(UriPartial.Path) adds a slash to origin-only URLs.
        // OIDC issuer identifiers are compared exactly with the discovery document,
        // and providers such as Pocket ID publish the origin without that slash.
        if (issuer.AbsolutePath == "/" && string.IsNullOrEmpty(issuer.Query) && string.IsNullOrEmpty(issuer.Fragment))
        {
            return issuer.GetLeftPart(UriPartial.Authority);
        }

        return issuerPath;
    }

    private static Uri ValidateEndpoint(string? value, string issuer, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException($"The issuer discovery document has an invalid {description} endpoint.");
        }

        return endpoint;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes) throw new InvalidOperationException("The identity provider token response is too large.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new InvalidOperationException("The identity provider token response is too large.");
            output.Write(buffer, 0, read);
        }
    }

    private static void ValidateMetadataIssuer(OpenIdConnectConfiguration metadata, string configuredIssuer)
    {
        if (!string.Equals(metadata.Issuer, configuredIssuer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The discovery document issuer does not match the configured issuer.");
        }
    }

    private static string Require(string value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value.Trim()
        : throw new InvalidOperationException($"Set the {name} before starting OIDC login.");
    private static string EnsureOpenIdScope(string scopes)
    {
        var requested = (scopes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return requested.Contains("openid", StringComparer.Ordinal) ? string.Join(' ', requested) : "openid " + string.Join(' ', requested);
    }
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("token_type")] string? TokenType);
}

/// <summary>A verified identity and its user information claims.</summary>
public sealed record VerifiedOidcIdentity(string ProviderId, string Issuer, string Subject, string Username, string? DisplayName, string? PictureUrl, IReadOnlySet<string> Groups)
{
    internal static VerifiedOidcIdentity Create(string providerId, string usernameClaim, string groupsClaim, string issuer, string subject, ClaimsPrincipal idToken)
    {
        var username = First(usernameClaim, "preferred_username", "username", "sub")
            ?? throw new InvalidOperationException("The verified identity has no usable username.");
        var groups = All(groupsClaim).SelectMany(ParseGroupValue).ToHashSet(StringComparer.Ordinal);
        var picture = First("picture");
        return new VerifiedOidcIdentity(providerId, issuer, subject, username, First("name", "display_name"), picture, groups);

        string? First(params string[] names)
        {
            foreach (var name in names)
            {
                var claim = idToken.FindFirst(name)?.Value;
                if (!string.IsNullOrWhiteSpace(claim)) return claim;
            }
            return null;
        }

        IEnumerable<string> All(string name) => idToken.FindAll(name).Select(c => c.Value);
    }

    private static IEnumerable<string> ParseGroupValue(string value)
    {
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(value) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        return [value];
    }
}

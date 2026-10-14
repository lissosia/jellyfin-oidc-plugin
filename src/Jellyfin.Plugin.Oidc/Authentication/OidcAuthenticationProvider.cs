using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.Oidc.Authentication;

/// <summary>Adapts one-time OIDC bridge credentials to Jellyfin's native session authentication path.</summary>
public sealed class OidcAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
{
    private readonly MockLoginTicketStore _ticketStore;

    /// <summary>Initializes a new instance of the <see cref="OidcAuthenticationProvider"/> class.</summary>
    public OidcAuthenticationProvider(MockLoginTicketStore ticketStore) => _ticketStore = ticketStore;

    /// <inheritdoc />
    public string Name => "Pocket ID OIDC";

    /// <inheritdoc />
    public bool IsEnabled => Plugin.Instance?.Configuration.EnableOidc == true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        throw new AuthenticationException("A resolved Jellyfin user is required for OIDC authentication.");
    }

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
    {
        if (!IsEnabled
            || resolvedUser is null
            || string.IsNullOrWhiteSpace(resolvedUser.Username)
            || !_ticketStore.TryConsume(password, resolvedUser.Id))
        {
            throw new AuthenticationException("Invalid or expired OIDC login credential.");
        }

        return Task.FromResult(new ProviderAuthenticationResult
        {
            Username = resolvedUser.Username,
            DisplayName = resolvedUser.Username
        });
    }

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword)
    {
        throw new AuthenticationException("Pocket ID manages this user's credentials.");
    }
}

using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.Oidc.Authentication;

/// <summary>
/// Adapts one mock external identity result to Jellyfin's native authentication flow.
/// </summary>
public sealed class MockAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
{
    private readonly MockLoginTicketStore _ticketStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="MockAuthenticationProvider"/> class.
    /// </summary>
    public MockAuthenticationProvider(MockLoginTicketStore ticketStore)
    {
        _ticketStore = ticketStore;
    }

    /// <inheritdoc />
    public string Name => "OIDC Phase 0 Mock";

    /// <inheritdoc />
    public bool IsEnabled => Plugin.Instance?.Configuration.EnablePhaseZeroMockLogin == true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        // Never authenticate without Jellyfin resolving the requested local account first.
        throw new AuthenticationException("A resolved Jellyfin user is required.");
    }

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
    {
        if (!IsEnabled
            || resolvedUser is null
            || string.IsNullOrWhiteSpace(resolvedUser.Username)
            || !_ticketStore.TryConsume(password, resolvedUser.Id))
        {
            throw new AuthenticationException("Invalid or expired mock login credential.");
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
        throw new AuthenticationException("The mock OIDC provider does not manage Jellyfin passwords.");
    }
}

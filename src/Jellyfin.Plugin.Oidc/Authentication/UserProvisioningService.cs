using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Authentication;

/// <summary>Resolves immutable OIDC identities and applies explicitly configured account and admin rules.</summary>
public sealed class UserProvisioningService
{
    private static readonly Regex ValidUsername = new("^[\\p{L}\\p{N}_.@ -]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private readonly IUserManager _userManager;
    private readonly AvatarSyncService _avatarSyncService;
    private readonly ILogger<UserProvisioningService> _logger;

    /// <summary>Initializes a new instance of the <see cref="UserProvisioningService"/> class.</summary>
    public UserProvisioningService(IUserManager userManager, AvatarSyncService avatarSyncService, ILogger<UserProvisioningService> logger)
    {
        _userManager = userManager;
        _avatarSyncService = avatarSyncService;
        _logger = logger;
    }

    /// <summary>Finds or creates a linked account, applies mapped admin access, and imports its avatar.</summary>
    public async Task<User> ResolveAndSyncAsync(VerifiedOidcIdentity identity, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("The OIDC plugin is not initialized.");
        await SyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = plugin.Configuration;
            var link = configuration.IdentityLinks.FirstOrDefault(item =>
                string.Equals(item.Issuer, identity.Issuer, StringComparison.Ordinal)
                && string.Equals(item.Subject, identity.Subject, StringComparison.Ordinal));
            if (!identity.Groups.Contains(configuration.AllowedGroup, StringComparer.Ordinal))
            {
                if (link is not null && _userManager.GetUserById(link.UserId) is { } linkedUser)
                {
                    await RevokeManagedAdministratorAsync(configuration, linkedUser).ConfigureAwait(false);
                }
                throw new InvalidOperationException("Your Pocket ID account is not in the group allowed to use Jellyfin.");
            }

            User user;
            if (link is not null)
            {
                user = _userManager.GetUserById(link.UserId)
                    ?? throw new InvalidOperationException("The linked Jellyfin account no longer exists. Ask an administrator to review the identity link.");
            }
            else
            {
                if (!configuration.EnableUserProvisioning)
                {
                    throw new InvalidOperationException("This Pocket ID identity is not linked to a Jellyfin user and account creation is disabled.");
                }

                if (identity.Username is null || !ValidUsername.IsMatch(identity.Username) || identity.Username.StartsWith(' ') || identity.Username.EndsWith(' '))
                {
                    throw new InvalidOperationException("Pocket ID did not provide a valid preferred_username for Jellyfin.");
                }

                if (_userManager.GetUserByName(identity.Username) is not null)
                {
                    throw new InvalidOperationException("A Jellyfin account already uses this username but is not linked to this Pocket ID identity. Link it explicitly before signing in.");
                }

                try
                {
                    user = await _userManager.CreateUserAsync(identity.Username).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException("Jellyfin could not create the account from Pocket ID's preferred_username.");
                }

                user.AuthenticationProviderId = typeof(OidcAuthenticationProvider).FullName!;
                configuration.IdentityLinks.Add(new OidcIdentityLink
                {
                    Issuer = identity.Issuer,
                    Subject = identity.Subject,
                    UserId = user.Id
                });
                plugin.UpdateConfiguration(configuration);
            }

            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            await ApplyAdministratorMappingAsync(configuration, identity, user).ConfigureAwait(false);
            try
            {
                await _avatarSyncService.ImportIfConfiguredAsync(configuration, identity, user, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Profile picture synchronization failed for Jellyfin user {UserId}.", user.Id);
            }
            return user;
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private async Task ApplyAdministratorMappingAsync(PluginConfiguration configuration, VerifiedOidcIdentity identity, User user)
    {
        if (!configuration.EnableAdministratorGroupMapping)
        {
            await RevokeManagedAdministratorAsync(configuration, user).ConfigureAwait(false);
            return;
        }

        var hasAdminGroup = identity.Groups.Contains(configuration.AdministratorGroup, StringComparer.Ordinal);
        var policy = _userManager.GetUserDto(user).Policy;
        if (hasAdminGroup)
        {
            if (!policy.IsAdministrator)
            {
                policy.IsAdministrator = true;
                if (!configuration.PluginManagedAdministrators.Contains(user.Id)) configuration.PluginManagedAdministrators.Add(user.Id);
                Plugin.Instance!.UpdateConfiguration(configuration);
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            }
        }
        else if (configuration.PluginManagedAdministrators.Remove(user.Id))
        {
            policy.IsAdministrator = false;
            Plugin.Instance!.UpdateConfiguration(configuration);
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }
    }

    private async Task RevokeManagedAdministratorAsync(PluginConfiguration configuration, User user)
    {
        if (!configuration.PluginManagedAdministrators.Remove(user.Id)) return;
        var policy = _userManager.GetUserDto(user).Policy;
        policy.IsAdministrator = false;
        Plugin.Instance!.UpdateConfiguration(configuration);
        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
    }

}

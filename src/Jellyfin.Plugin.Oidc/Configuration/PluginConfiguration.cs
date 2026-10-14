using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>
/// Configuration for the native OIDC plugin.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets whether the Phase 0 mock identity bridge is enabled.
    /// Keep this disabled outside an isolated compatibility spike.
    /// </summary>
    public bool EnablePhaseZeroMockLogin { get; set; }

    /// <summary>Gets or sets whether the configured OpenID Connect provider is enabled.</summary>
    public bool EnableOidc { get; set; }

    /// <summary>Gets or sets the OpenID Connect issuer URL.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets the public Jellyfin URL used for redirect URI validation.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the OpenID Connect client identifier.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the data-protected client secret.</summary>
    public string ProtectedClientSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets whether users in the allowed group may be provisioned.</summary>
    public bool EnableUserProvisioning { get; set; }

    /// <summary>Gets or sets the group required to sign in.</summary>
    public string AllowedGroup { get; set; } = "jellyfin_users";

    /// <summary>Gets or sets the group mapped to Jellyfin administrator.</summary>
    public string AdministratorGroup { get; set; } = "jellyfin_admins";

    /// <summary>Gets or sets whether the administrator group mapping is enabled.</summary>
    public bool EnableAdministratorGroupMapping { get; set; }

    /// <summary>Gets or sets the avatar synchronization mode.</summary>
    public string AvatarSyncMode { get; set; } = "ImportOnce";

    /// <summary>Gets or sets the immutable issuer and subject to Jellyfin user ID links.</summary>
    public List<OidcIdentityLink> IdentityLinks { get; set; } = [];

    /// <summary>Gets or sets the users for whom this plugin assigned administrator access.</summary>
    public List<Guid> PluginManagedAdministrators { get; set; } = [];
}

/// <summary>XML-serializable link from an immutable issuer/subject pair to a Jellyfin user.</summary>
public sealed class OidcIdentityLink
{
    /// <summary>Gets or sets the issuer URL.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets the immutable provider subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the linked Jellyfin user ID.</summary>
    public Guid UserId { get; set; }
}

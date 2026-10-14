using System.Globalization;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Oidc;

/// <summary>
/// Jellyfin plugin entry point.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin identifier used by Jellyfin and its configuration page.
    /// </summary>
    public static readonly Guid PluginGuid = Guid.Parse("b51a78b4-74b0-4e18-9f69-8e7f79203856");
    private readonly IDataProtector _secretProtector;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IDataProtectionProvider dataProtectionProvider)
        : base(applicationPaths, xmlSerializer)
    {
        _secretProtector = dataProtectionProvider.CreateProtector("Jellyfin.Plugin.Oidc.ClientSecret.v1");
        Instance = this;
    }

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the decrypted client secret for protocol use.</summary>
    public string GetClientSecret()
    {
        if (string.IsNullOrEmpty(Configuration.ProtectedClientSecret)) return string.Empty;
        try { return _secretProtector.Unprotect(Configuration.ProtectedClientSecret); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("The saved OIDC client secret cannot be decrypted. Enter it again in the settings page.");
        }
    }

    /// <summary>Stores a client secret protected by Jellyfin's data-protection keys.</summary>
    public void SetClientSecret(string secret)
    {
        Configuration.ProtectedClientSecret = string.IsNullOrEmpty(secret) ? string.Empty : _secretProtector.Protect(secret);
        UpdateConfiguration(Configuration);
    }

    /// <inheritdoc />
    public override string Name => "OpenID Connect";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "oidc-settings",
            DisplayName = "OpenID Connect",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Web.configPage.html",
                GetType().Namespace)
        };
    }
}

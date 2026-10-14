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
}

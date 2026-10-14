using Jellyfin.Plugin.Oidc.Authentication;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Oidc;

/// <summary>
/// Registers services required by the plugin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MockLoginTicketStore>();
        serviceCollection.AddSingleton<MockAuthenticationProvider>();
        serviceCollection.AddSingleton<IAuthenticationProvider>(services =>
            services.GetRequiredService<MockAuthenticationProvider>());
    }
}

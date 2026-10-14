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
        serviceCollection.AddSingleton<OidcTransactionStore>();
        serviceCollection.AddSingleton<OidcService>();
        serviceCollection.AddSingleton<UserProvisioningService>();
        serviceCollection.AddSingleton<AvatarSyncService>();
        serviceCollection.AddSingleton<OidcAuthenticationProvider>();
        serviceCollection.AddSingleton<IAuthenticationProvider>(services =>
            services.GetRequiredService<OidcAuthenticationProvider>());
        serviceCollection.AddHttpClient("OidcProtocol", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            });
        serviceCollection.AddHttpClient("OidcAvatar", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            });
    }
}

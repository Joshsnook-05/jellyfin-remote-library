using Jellyfin.Plugin.RemoteLibrary.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.RemoteLibrary;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(RemoteLibraryService.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            });
        serviceCollection.AddSingleton<RemoteLibraryService>();
        serviceCollection.AddHostedService<RemoteLibraryStartupSyncService>();
        serviceCollection.AddSingleton<IStartupFilter, RemoteLibraryUiStartupFilter>();
    }
}

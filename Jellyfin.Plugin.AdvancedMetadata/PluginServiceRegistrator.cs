using Jellyfin.Plugin.AdvancedMetadata.Scanning;
using Jellyfin.Plugin.AdvancedMetadata.Store;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AdvancedMetadata;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MetadataStore>();
        serviceCollection.AddSingleton<IScheduledTask, ScanTask>();
    }
}

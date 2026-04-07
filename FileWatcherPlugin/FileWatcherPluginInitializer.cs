using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Purewwk.Plugin.Abstractions;
using Purewwk.Plugin.Abstractions;

namespace Purewwk.Plugins.FileWatcher;

public class FileWatcherPluginInitializer : IPluginInitializer
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<FileWatcherService>();
    }
}




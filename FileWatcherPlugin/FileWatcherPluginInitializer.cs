using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Purewwk.Plugin;
using Purewwk.Plugin;

namespace Purewwk.Plugins.FileWatcher;

public class FileWatcherPluginInitializer : IPluginInitializer
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<FileWatcherService>();
    }
}




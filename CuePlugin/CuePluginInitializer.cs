using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Purewwk.Plugin.Abstractions;

namespace Purewwk.Plugins.Cue;

public class CuePluginInitializer : IPluginInitializer
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ICueService, CueService>();
        services.AddSingleton<ICueFolderService, CueFolderService>();
        services.AddSingleton<IIndexingPlugin, CueIndexingPlugin>();
    }
}




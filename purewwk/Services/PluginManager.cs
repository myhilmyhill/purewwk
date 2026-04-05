using System.Reflection;
using Purewwk.Plugin.Abstractions;

namespace Purewwk.Services;

public class PluginManager
{
    private readonly IEnumerable<IPlaybackPlugin> _playbackPlugins;
    private readonly ILogger<PluginManager> _logger;

    public PluginManager(ILogger<PluginManager> logger, IEnumerable<IPlaybackPlugin> playbackPlugins)
    {
        _logger = logger;
        _playbackPlugins = playbackPlugins;
        _logger.LogInformation("PluginManager initialized with {Count} playback plugins.", _playbackPlugins.Count());
    }

    public static void LoadPlugins(IServiceCollection services, IConfiguration configuration)
    {
        var workingDir = configuration["WorkingDirectory"] ?? AppContext.BaseDirectory;
        var pluginsDir = Path.Combine(workingDir, "plugins");
        
        if (!Directory.Exists(pluginsDir))
        {
            Directory.CreateDirectory(pluginsDir);
        }

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
                var initializers = assembly.GetTypes()
                    .Where(t => typeof(IPluginInitializer).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var initType in initializers)
                {
                    var initializer = (IPluginInitializer?)Activator.CreateInstance(initType);
                    initializer?.RegisterServices(services, configuration);
                    Console.WriteLine($"Loaded plugin initializer: {initType.FullName} from {dll}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading plugin from {dll}: {ex.Message}");
            }
        }
    }

    public IPlaybackPlugin? GetPluginForExtension(string extension)
    {
        var ext = extension.ToLowerInvariant();
        if (!ext.StartsWith(".")) ext = "." + ext;
        return _playbackPlugins.FirstOrDefault(p => p.CanHandle(ext));
    }
}

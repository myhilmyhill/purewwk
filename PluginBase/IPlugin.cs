
namespace Purewwk.Plugin;

public interface IPlugin
{
    string Name { get; }
    string[] SupportedExtensions { get; }
    bool CanHandle(string extension);
}

/// <summary>
/// Framework-agnostic playback response
/// </summary>
public class PlaybackResponse
{
    public string ContentType { get; init; } = string.Empty;
    public string? Content { get; init; }
    public System.IO.Stream? Stream { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
}

/// <summary>
/// Capability to handle playback for a MediaItem
/// </summary>
public interface IPlayablePlugin : IPlugin
{
    Task<PlaybackResponse> HandlePlaybackAsync(MediaItem item, Dictionary<string, string> queryParams);
    string GetPlayerType(string extension);
    string GetMimeType(string extension);
}

/// <summary>
/// Capability to contribute items to the index
/// </summary>
public interface IIndexingPlugin : IPlugin
{
    IEnumerable<MediaItem> GetEntries(string path);
}

public interface IPluginInitializer
{
    void RegisterServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration);
}


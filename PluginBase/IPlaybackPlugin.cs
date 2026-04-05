using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Purewwk.Plugin.Abstractions;

public class MediaFileMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public Dictionary<string, string> Attributes { get; set; } = new();
}

public interface IPlaybackPlugin
{
    string Name { get; }
    string[] SupportedExtensions { get; }
    bool CanHandle(string extension);
    
    // Backend: Process playback (generate playlist, stream, etc.)
    Task<IActionResult> HandlePlaybackAsync(MediaFileMetadata metadata, HttpContext context);
    
    // Frontend related info
    // Returns the player type (e.g., "hls", "audio-tag") so frontend knows what to do
    string GetPlayerType(string extension);

    // MIME type for downloads
    string GetMimeType(string extension);
}

public interface IPluginInitializer
{
    void RegisterServices(IServiceCollection services, IConfiguration configuration);
}

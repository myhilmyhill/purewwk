using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Purewwk.Plugin.Abstractions;

namespace Purewwk.Plugins.Fluidsynth;

public class FluidsynthPlaybackPlugin : IPlaybackPlugin
{
    private readonly FluidsynthService _fluidsynthService;
    private readonly ILogger<FluidsynthPlaybackPlugin> _logger;

    public FluidsynthPlaybackPlugin(FluidsynthService fluidsynthService, ILogger<FluidsynthPlaybackPlugin> logger)
    {
        _fluidsynthService = fluidsynthService;
        _logger = logger;
    }

    public string Name => "Fluidsynth MIDI Player";

    public string[] SupportedExtensions => new[] { ".mid", ".midi" };

    public bool CanHandle(string extension)
    {
        return SupportedExtensions.Contains(extension.ToLowerInvariant());
    }

    public async Task<IActionResult> HandlePlaybackAsync(MediaFileMetadata metadata, HttpContext context)
    {
        int bitRate = 128;
        if (context.Request.Query.TryGetValue("bitRate", out var brStr))
        {
            int.TryParse(brStr, out bitRate);
        }

        try
        {
            var playlist = await _fluidsynthService.GenerateHlsPlaylist(metadata, new[] { bitRate });
            
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, proxy-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
            
            return new ContentResult
            {
                Content = playlist,
                ContentType = "application/vnd.apple.mpegurl"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fluidsynth Plugin error: {Message}", ex.Message);
            return new ObjectResult(ex.Message) { StatusCode = 500 };
        }
    }

    public string GetPlayerType(string extension) => "hls";

    public string GetMimeType(string extension) => "audio/midi";
}

public class FluidsynthPluginInitializer : IPluginInitializer
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<FluidsynthService>();
        services.AddSingleton<IPlaybackPlugin, FluidsynthPlaybackPlugin>();
    }
}

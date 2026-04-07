using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Purewwk.Plugin;

namespace Purewwk.Plugins.Fluidsynth;

public class FluidsynthPlaybackPlugin : IPlayablePlugin
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

    public async Task<PlaybackResponse> HandlePlaybackAsync(MediaItem item, Dictionary<string, string> queryParams)
    {
        int bitRate = 128;
        if (queryParams.TryGetValue("bitRate", out var brStr))
        {
            int.TryParse(brStr, out bitRate);
        }

        try
        {
            var playlist = await _fluidsynthService.GenerateHlsPlaylist(item, new[] { bitRate });
            
            return new PlaybackResponse
            {
                Content = playlist,
                ContentType = "application/vnd.apple.mpegurl",
                Headers = new Dictionary<string, string>
                {
                    { "Cache-Control", "no-store, no-cache, must-revalidate, proxy-revalidate" },
                    { "Pragma", "no-cache" },
                    { "Expires", "0" }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fluidsynth Plugin error");
            throw;
        }
    }

    public string GetPlayerType(string extension) => "fluidsynth";

    public string GetMimeType(string extension) => "audio/midi";
}

public class FluidsynthPluginInitializer : IPluginInitializer
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<FluidsynthService>();
        services.AddSingleton<IPlayablePlugin, FluidsynthPlaybackPlugin>();
    }
}




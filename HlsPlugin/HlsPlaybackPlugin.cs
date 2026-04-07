using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Purewwk.Plugin;

namespace Purewwk.Plugins.Hls;

public class HlsPlaybackPlugin : IPlayablePlugin
{
    private readonly HlsService _hlsService;
    private readonly ILogger<HlsPlaybackPlugin> _logger;

    public HlsPlaybackPlugin(HlsService hlsService, ILogger<HlsPlaybackPlugin> logger)
    {
        _hlsService = hlsService;
        _logger = logger;
    }

    public string Name => "HLS Transcoding Player";

    public string[] SupportedExtensions => new[] 
    { 
        ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wv", ".ape", ".wma" 
    };

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
        string? audioTrack = queryParams.GetValueOrDefault("audioTrack");

        try
        {
            var playlist = await _hlsService.GenerateHlsPlaylist(item, new[] { bitRate }, audioTrack);
            
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
            _logger.LogError(ex, "HLS Plugin error");
            throw;
        }
    }

    public string GetPlayerType(string extension) => "hls";

    public string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".wv" => "audio/x-wavpack",
            ".ape" => "audio/x-ape",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream"
        };
    }
}

public class HlsCleanupHostedService : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly HlsService _hlsService;

    public HlsCleanupHostedService(HlsService hlsService)
    {
        _hlsService = hlsService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hlsService.StartCacheCleanupAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public class HlsPluginInitializer : IPluginInitializer
{
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IHlsCacheStorage>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<HlsCacheStorage>>();
            var maxSize = configuration.GetValue<int>("HlsCache:MaxSize", 100);
            var maxAgeMinutes = configuration.GetValue<int>("HlsCache:MaxAgeMinutes", 60);
            return new HlsCacheStorage(logger, configuration, maxSize, TimeSpan.FromMinutes(maxAgeMinutes));
        });
        services.AddSingleton<HlsService>();
        services.AddSingleton<IPlayablePlugin, HlsPlaybackPlugin>();
        services.AddHostedService<HlsCleanupHostedService>();
    }
}




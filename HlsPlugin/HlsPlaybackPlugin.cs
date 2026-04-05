using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Purewwk.Plugin.Abstractions;

namespace Purewwk.Plugins.Hls;

public class HlsPlaybackPlugin : IPlaybackPlugin
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

    public async Task<IActionResult> HandlePlaybackAsync(MediaFileMetadata metadata, HttpContext context)
    {
        int bitRate = 128;
        if (context.Request.Query.TryGetValue("bitRate", out var brStr))
        {
            int.TryParse(brStr, out bitRate);
        }
        string? audioTrack = context.Request.Query["audioTrack"];

        try
        {
            var playlist = await _hlsService.GenerateHlsPlaylist(metadata, new[] { bitRate }, audioTrack);
            
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
            _logger.LogError(ex, "HLS Plugin error: {Message}", ex.Message);
            return new ObjectResult(ex.Message) { StatusCode = 500 };
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
        services.AddSingleton<IPlaybackPlugin, HlsPlaybackPlugin>();
        services.AddHostedService<HlsCleanupHostedService>();
    }
}

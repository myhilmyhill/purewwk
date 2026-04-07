using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Purewwk.Plugin;

namespace Purewwk.Plugins.Hls;

public class HlsService : HlsServiceBase
{
    private readonly IHlsCacheStorage _cacheStorage;
    private readonly bool _cacheEnabled;

    public HlsService(ILogger<HlsService> logger, IConfiguration configuration, IHttpContextAccessor httpContextAccessor, IHlsCacheStorage cacheStorage)
        : base(logger, configuration, httpContextAccessor)
    {
        _cacheStorage = cacheStorage;
        _cacheEnabled = configuration.GetValue<bool>("HlsCache:Enabled", true);
    }

    public async Task<string> GenerateHlsPlaylist(MediaItem item, int[] bitRates, string? audioTrack)
    {
        var id = item.Id;
        var fullPath = item.Path;
        var fileInfo = item as MediaFile;
        var isCueTrack = fileInfo?.StartTime.HasValue == true;
        var cueStart = fileInfo?.StartTime ?? 0;
        var cueDuration = fileInfo?.Duration ?? 0;

        var variantKey = BuildVariantKey(bitRates, audioTrack);
        var key = $"{id}/{variantKey}";
        
        string workingDir = string.IsNullOrEmpty(_configuration["WorkingDirectory"]) ? AppContext.BaseDirectory : _configuration["WorkingDirectory"] ?? AppContext.BaseDirectory;
        var cacheDir = Path.Combine(workingDir, $"hls_segments{key}");
        var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");
        
        // Calculate Base URL components
        var baseUrl = GetBaseUrl();
        var encodedSuffix = Uri.EscapeDataString(key + "/");
        var segmentBaseUrl = $"{baseUrl}{encodedSuffix}";
        
        bool startNewProcess = true;

        lock (_processLock)
        {
            if (TryGetActiveProcess(id, out var active))
            {
                if (active.VariantKey == variantKey)
                {
                    _logger.LogDebug("Reusing existing FFmpeg process for id: {Id}, variant: {Variant}", id, variantKey);
                    startNewProcess = false;
                }
                else
                {
                    _logger.LogInformation("Cancelling existing FFmpeg process (variant mismatch) for id: {Id}", id);
                    CancelAndRemoveActiveProcess(id);
                }
            }
        }

        if (startNewProcess)
        {
            // If cache is enabled, check for existing playlist
            if (_cacheEnabled)
            {
                var cachedEntry = await _cacheStorage.GetAsync(key);
                if (cachedEntry != null)
                {
                    _logger.LogDebug("Found complete cached HLS playlist for key: {key}", key);
                    return cachedEntry.Content.Replace("segment_", $"{segmentBaseUrl}segment_");
                }
            }

            // Create directory
            Directory.CreateDirectory(cacheDir);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Media file not found on disk: " + fullPath);
            }

            _logger.LogDebug("Using file directly for HLS processing: {FullPath}", fullPath);
            
            // FFmpeg command for HLS
            string ffmpegArgs;
            var seekArgs = "";
            
            if (isCueTrack)
            {
                 seekArgs = $"-ss {cueStart}";
                 if (cueDuration > 0)
                 {
                     seekArgs += $" -t {cueDuration}";
                 }
            }

            var inputArgs = isCueTrack ? $"{seekArgs} -i \"{fullPath}\"" : $"-i \"{fullPath}\"";
            var commonHlsArgs = $"-f hls -hls_time 3 -hls_list_size 0 -start_number 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\"";

            if (bitRates.Length > 0)
            {
                var bitRate = bitRates[0];
                ffmpegArgs = $"-y -v error {inputArgs} -vn -af loudnorm -c:a aac -b:a {bitRate}k {commonHlsArgs} \"{playlistPath}\"";
            }
            else
            {
                ffmpegArgs = $"-y -v error {inputArgs} -vn -af loudnorm -c:a aac {commonHlsArgs} \"{playlistPath}\"";
            }

            _logger.LogDebug("FFmpeg HLS command: ffmpeg {FfmpegArgs}", ffmpegArgs);

            // Run FFmpeg in background
            var cts = new CancellationTokenSource();
            RegisterActiveProcess(id, cts, variantKey);
            
            var ffmpegTask = RunFfmpegInBackground(ffmpegArgs, cacheDir, cts.Token);
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await ffmpegTask;
                    _logger.LogInformation("FFmpeg background conversion completed");
                    UnregisterActiveProcess(id, cts);
                    
                    if (_cacheEnabled && File.Exists(playlistPath))
                    {
                        var finalPlaylistContent = await File.ReadAllTextAsync(playlistPath);
                        var finalCacheEntry = new HlsCacheEntry
                        {
                            Content = finalPlaylistContent,
                            CacheDirectory = cacheDir,
                            CreatedAt = DateTime.Now,
                            LastAccessed = DateTime.Now
                        };
                        await _cacheStorage.SetAsync(key, finalCacheEntry);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FFmpeg background conversion failed");
                    UnregisterActiveProcess(id, cts);
                }
            });
        }
        
        await WaitForFirstSegment(id, cacheDir, playlistPath, minSegments: 2);
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        return playlistContent.Replace("segment_", $"{segmentBaseUrl}segment_");
    }

    private string BuildVariantKey(int[] bitRates, string? audioTrack)
    {
        var bitRateStr = bitRates.Length > 0 ? bitRates[0].ToString() : "default"; 
        var audioTrackStr = audioTrack ?? "default";
        return $"{bitRateStr}_{audioTrackStr}";
    }

    public Task StartCacheCleanupAsync()
    {
        if (!_cacheEnabled) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await _cacheStorage.CleanupExpiredEntriesAsync();
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in HLS cache cleanup");
                    await Task.Delay(TimeSpan.FromMinutes(10));
                }
            }
        });
        
        return Task.CompletedTask;
    }


    private async Task RunFfmpegInBackground(string args, string cacheDir, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                ?? _configuration["FFmpeg:Path"]
                ?? "ffmpeg";

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogTrace("FFmpeg Output: {Data}", e.Data); };
            
            var errorBuilder = new System.Text.StringBuilder();
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogTrace("FFmpeg error: {Data}", e.Data);
                    lock(errorBuilder) { if (errorBuilder.Length < 4096) errorBuilder.AppendLine(e.Data); }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            
            try
            {
                await process.WaitForExitAsync(combinedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                if (cancellationToken.IsCancellationRequested) return;
                throw new Exception("FFmpeg timeout");
            }

            if (process.ExitCode != 0)
            {
                string errorLog;
                lock(errorBuilder) { errorLog = errorBuilder.ToString(); }
                _logger.LogError("FFmpeg error {ExitCode}: {Log}", process.ExitCode, errorLog);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg error");
        }
        finally
        {
            process?.Dispose();
        }
    }

}

using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Purewwk.Plugin.Abstractions;

namespace Purewwk.Plugins.Hls;

public class HlsService
{
    private readonly ILogger<HlsService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHlsCacheStorage _cacheStorage;
    private readonly bool _cacheEnabled;
    
    private readonly Dictionary<string, (CancellationTokenSource Cts, string VariantKey, DateTime StartTime)> _activeProcesses = new();
    private readonly object _processLock = new object();

    public HlsService(ILogger<HlsService> logger, IConfiguration configuration, IHttpContextAccessor httpContextAccessor, IHlsCacheStorage cacheStorage)
    {
        _logger = logger;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
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
            if (_activeProcesses.TryGetValue(id, out var active))
            {
                if (active.VariantKey == variantKey)
                {
                    _logger.LogDebug("Reusing existing FFmpeg process for id: {Id}, variant: {Variant}", id, variantKey);
                    startNewProcess = false;
                }
                else
                {
                    _logger.LogInformation("Cancelling existing FFmpeg process (variant mismatch) for id: {Id}", id);
                    active.Cts.Cancel();
                    _activeProcesses.Remove(id);
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
        
        await WaitForFirstSegment(id, cacheDir, playlistPath);
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

    private string GetBaseUrl()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) throw new Exception("HttpContext is null");
        
        var request = httpContext.Request;
        var pathBase = request.PathBase.Value;
        return $"{pathBase}/hls?key=";
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

    private async Task WaitForFirstSegment(string id, string cacheDir, string playlistPath)
    {
        const int MinSegments = 2;
        var timeout = TimeSpan.FromSeconds(30);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            if (File.Exists(playlistPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(playlistPath);
                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var segments = lines.Where(l => l.EndsWith(".ts") && !l.StartsWith("#")).ToList();
                    var isComplete = lines.Any(l => l.Contains("#EXT-X-ENDLIST"));
                    
                    if (segments.Count >= MinSegments || (segments.Count > 0 && isComplete))
                    {
                        var segmentPath = Path.Combine(cacheDir, segments.Last());
                        if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 0) return;
                    }

                    if (segments.Count > 0 && (DateTime.Now - startTime).TotalSeconds > 2.0) return;
                }
                catch { }
            }

            bool active;
            lock(_processLock) { active = _activeProcesses.ContainsKey(id); }
            if (!active) throw new Exception("FFmpeg exited early");

            await Task.Delay(200);
        }
        throw new Exception("HLS timeout");
    }

    private void RegisterActiveProcess(string id, CancellationTokenSource cts, string variantKey)
    {
        lock (_processLock)
        {
            if (_activeProcesses.Count >= 4)
            {
                var oldest = _activeProcesses.OrderBy(kvp => kvp.Value.StartTime).First();
                oldest.Value.Cts.Cancel();
                _activeProcesses.Remove(oldest.Key);
            }
            _activeProcesses[id] = (cts, variantKey, DateTime.Now);
        }
    }

    private void UnregisterActiveProcess(string id, CancellationTokenSource cts)
    {
        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var active) && active.Cts == cts) _activeProcesses.Remove(id);
        }
    }
}

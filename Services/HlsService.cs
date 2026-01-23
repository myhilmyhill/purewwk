using System.Diagnostics;
using System.Linq;

namespace repos.Services;

public class HlsService
{
    private readonly ILogger<HlsService> _logger;
    private readonly IConfiguration _configuration;
    private readonly LuceneService _luceneService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHlsCacheStorage _cacheStorage;
    private readonly bool _cacheEnabled;
    
    // FFmpegプロセス管理用
    private readonly Dictionary<string, (CancellationTokenSource Cts, string VariantKey, DateTime StartTime)> _activeProcesses = new();
    private readonly object _processLock = new object();

    public HlsService(ILogger<HlsService> logger, IConfiguration configuration, LuceneService luceneService, IHttpContextAccessor httpContextAccessor, IHlsCacheStorage cacheStorage)
    {
        _logger = logger;
        _configuration = configuration;
        _luceneService = luceneService;
        _httpContextAccessor = httpContextAccessor;
        _cacheStorage = cacheStorage;
        _cacheEnabled = configuration.GetValue<bool>("HlsCache:Enabled", true);
    }

    public async Task<string> GenerateHlsPlaylist(string id, int[] bitRates, string? audioTrack)
    {
        var variantKey = BuildVariantKey(bitRates, audioTrack);
        var key = $"{id}/{variantKey}";
        
        string workingDir = string.IsNullOrEmpty(_configuration["WorkingDirectory"]) ? AppContext.BaseDirectory : _configuration["WorkingDirectory"] ?? AppContext.BaseDirectory;
        var cacheDir = Path.Combine(workingDir, $"hls_segments{key}");
        var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");
        
        // Calculate Base URL components here to ensure availability for both cache hit and new generation
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
            // キャッシュヒット時の完全性チェック
            if (_cacheEnabled)
            {
                var cachedEntry = await _cacheStorage.GetAsync(key);
                // GetAsync returns null if incomplete, so if we get something, it's the full final playlist.
                if (cachedEntry != null)
                {
                    _logger.LogDebug("Found complete cached HLS playlist for key: {key}", key);
                    // Critical Fix: Inject absolute URLs into cached content as the file on disk has relative paths
                    return cachedEntry.Content.Replace("segment_", $"{segmentBaseUrl}segment_");
                }
            }

            // Create directory
            Directory.CreateDirectory(cacheDir);
            _logger.LogDebug("HLS cache directory created: {CacheDir}", cacheDir);

            // Get file path from Lucene index
            _logger.LogDebug("Looking for file with id: {Id}", id);

            var fileDoc = _luceneService.GetDocumentById(id);
            if (fileDoc == null || fileDoc["isDir"] == "true")
            {
                throw new FileNotFoundException("Media file not found: " + id);
            }

            var fullPath = fileDoc["path"];

            // Check for CUE track metadata
            bool isCueTrack = fileDoc.ContainsKey("isCueTrack") && fileDoc["isCueTrack"] == "true";
            double cueStart = 0;
            double cueDuration = 0;

            if (isCueTrack)
            {
                if (fileDoc.ContainsKey("cueStart")) double.TryParse(fileDoc["cueStart"], out cueStart);
                if (fileDoc.ContainsKey("cueDuration")) double.TryParse(fileDoc["cueDuration"], out cueDuration);
                _logger.LogDebug("Detected CUE track. Source: {Source}, Start: {Start}, Duration: {Duration}", fullPath, cueStart, cueDuration);
            }

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
            // Explicitly set start_number to 0 to ensure consistency
        var commonHlsArgs = $"-f hls -hls_time 3 -hls_list_size 0 -start_number 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\"";

            if (bitRates.Length > 0)
            {
                var bitRate = bitRates[0];
                ffmpegArgs = $"-y -v error {inputArgs} -vn -c:a aac -b:a {bitRate}k {commonHlsArgs} \"{playlistPath}\"";
                _logger.LogDebug("Using single bitrate: {BitRate}k", bitRate);
            }
            else
            {
                ffmpegArgs = $"-y -v error {inputArgs} -vn -c:a aac {commonHlsArgs} \"{playlistPath}\"";
                _logger.LogDebug("Using default bitrate");
            }

            _logger.LogDebug("FFmpeg HLS command: ffmpeg {FfmpegArgs}", ffmpegArgs);

            // Run FFmpeg in background
            var cts = new CancellationTokenSource();
            RegisterActiveProcess(id, cts, variantKey);
            
            var ffmpegTask = RunFfmpegInBackground(ffmpegArgs, cacheDir, cts.Token);
            
            // Continue FFmpeg processing in background (don't await)
            _ = Task.Run(async () =>
            {
                try
                {
                    await ffmpegTask;
                    _logger.LogInformation("FFmpeg background conversion completed");
                    UnregisterActiveProcess(id, cts);
                    
                    // Update cache completion status (timestamp update)
                    if (_cacheEnabled && File.Exists(playlistPath))
                    {
                        var finalPlaylistContent = await File.ReadAllTextAsync(playlistPath);
                        // Note: We don't need to inject URLs here because SetAsync ignores the content 
                        // and GetAsync always reads from disk (which is relative).
                        // The injection happens only when serving the content (in GenerateHlsPlaylist).
                        
                        var finalCacheEntry = new HlsCacheEntry
                        {
                            Content = finalPlaylistContent,
                            CacheDirectory = cacheDir,
                            CreatedAt = DateTime.Now,
                            LastAccessed = DateTime.Now
                        };

                        await _cacheStorage.SetAsync(key, finalCacheEntry);
                        _logger.LogDebug("Updated final HLS cache timestamp after FFmpeg completion");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FFmpeg background conversion failed: {Message}", ex.Message);
                    // Ensure cleanup on error
                    UnregisterActiveProcess(id, cts);
                }
            });
        }
        
        // Wait for first segment to be created (or check if it exists if reusing process)
        await WaitForFirstSegment(id, cacheDir, playlistPath);

        // Read playlist content
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        
        // Inject absolute URLs manually
        playlistContent = playlistContent.Replace("segment_", $"{segmentBaseUrl}segment_");

        return playlistContent;
    }

    private string BuildVariantKey(int[] bitRates, string? audioTrack)
    {
        var bitRateStr = bitRates.Length > 0 ? bitRates[0].ToString() : "default"; // 単一ビットレート前提
        var audioTrackStr = audioTrack ?? "default";
        return $"{bitRateStr}_{audioTrackStr}";
    }

    public Task StartCacheCleanupAsync()
    {
        if (!_cacheEnabled) return Task.CompletedTask;

        // バックグラウンドで定期的なクリーンアップを実行
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await _cacheStorage.CleanupExpiredEntriesAsync();
                    await Task.Delay(TimeSpan.FromMinutes(1)); // 5分ごとにクリーンアップ
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in HLS cache cleanup: {Message}", ex.Message);
                    await Task.Delay(TimeSpan.FromMinutes(10)); // エラー時は10分待つ
                }
            }
        });
        
        return Task.CompletedTask;
    }

    private string GetBaseUrl()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var request = httpContext.Request;
            var pathBase = request.PathBase.Value; // for apps deployed in subdirectories

            // Use root-relative path to avoid scheme/host issues
            // This ensures hls.js requests /hls?key=... relative to the current domain
            var baseUrl = $"{pathBase}/hls?key=";
            _logger.LogDebug("Auto-detected base URL (root-relative): {BaseUrl}", baseUrl);
            return baseUrl;
        }
        else
        {
            throw new Exception("Unable to determine base URL - HttpContext is null");
        }
    }



    private async Task RunFfmpegInBackground(string args, string cacheDir, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            // Get FFmpeg path from environment variable or configuration, fallback to "ffmpeg"
            var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                ?? _configuration["FFmpeg:Path"]
                ?? "ffmpeg";

            _logger.LogDebug("Starting FFmpeg in background from path: {FfmpegPath}", ffmpegPath);
            _logger.LogDebug("FFmpeg args: {Args}", args);

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
            
            // Handle output to prevent deadlocks (buffer overflow)
            process.OutputDataReceived += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                     _logger.LogTrace("FFmpeg Output: {Data}", e.Data);
                }
            };
            
            var errorBuilder = new System.Text.StringBuilder();
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogTrace("FFmpeg Error: {Data}", e.Data);
                    lock(errorBuilder)
                    {
                        if (errorBuilder.Length < 4096) // Limit logging size
                            errorBuilder.AppendLine(e.Data);
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Much longer timeout for background processing (10 minutes)
            var timeout = TimeSpan.FromMinutes(10);
            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                try
                {
                    await process.WaitForExitAsync(combinedCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("FFmpeg background process was cancelled by user request, killing...");
                    if (!process.HasExited)
                    {
                         try { process.Kill(); } catch { }
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("FFmpeg background process timed out, killing...");
                    if (!process.HasExited)
                    {
                        try { process.Kill(); } catch { }
                    }
                    throw new Exception("FFmpeg background process timed out after 10 minutes");
                }
            }

            _logger.LogDebug("FFmpeg background process finished with exit code: {ExitCode}", process.ExitCode);

            if (process.ExitCode != 0)
            {
                string errorLog;
                lock(errorBuilder) { errorLog = errorBuilder.ToString(); }
                
                _logger.LogError("FFmpeg background error (exit code {ExitCode}): {Error}", process.ExitCode, errorLog);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logger.LogError("FFmpeg is not installed or not found in PATH for background processing");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running FFmpeg in background: {Message}", ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task WaitForFirstSegment(string id, string cacheDir, string playlistPath)
    {
        _logger.LogDebug("Waiting for HLS segments in directory: {CacheDir}", cacheDir);
        
        // Wait for at least 2 segments to ensure smooth transition from first segment
        // This prevents the "stall at 3 seconds" issue by ensuring the player knows about the next segment immediately.
        const int MinSegments = 2;
        
        var timeout = TimeSpan.FromSeconds(30);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            // Check if playlist exists and has content
            if (File.Exists(playlistPath))
            {
                try
                {
                    var playlistContent = await File.ReadAllTextAsync(playlistPath);
                    var lines = playlistContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    // Count valid segments
                    var segments = lines.Where(l => l.EndsWith(".ts") && !l.StartsWith("#")).ToList();
                    var isComplete = lines.Any(l => l.Contains("#EXT-X-ENDLIST"));
                    
                    // Success condition: Enough segments OR Stream finished (short file)
                    if (segments.Count >= MinSegments || (segments.Count > 0 && isComplete))
                    {
                        // Check if the latest required segment file exists
                        var lastSegment = segments.Last();
                        var segmentPath = Path.Combine(cacheDir, lastSegment);
                        
                        // Basic check to ensure FS has synced and file is written
                        if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 0)
                        {
                            _logger.LogDebug("Ready to serve: {Count} segments found. Last: {Last}", segments.Count, lastSegment);
                            return;
                        }
                    }
                    
                    // Fallback: If we have at least 1 segment and have waited > 2 seconds, just go to avoid long start delay.
                    // Ideally audio transcoding is fast enough to hit MinSegments in < 1s.
                    if (segments.Count > 0 && (DateTime.Now - startTime).TotalSeconds > 2.0)
                    {
                         _logger.LogDebug("Timeout waiting for {0} segments, proceeding with {1} to avoid start delay", MinSegments, segments.Count);
                         return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading playlist while waiting: {Message}", ex.Message);
                }
            }

            // Validating if the process is still running
            bool isProcessActive;
            lock (_processLock)
            {
                isProcessActive = _activeProcesses.ContainsKey(id);
            }

            if (!isProcessActive)
            {
                 // Process died/finished. Check one last time.
                 if (File.Exists(playlistPath))
                 {
                    var content = await File.ReadAllTextAsync(playlistPath);
                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var segmentFile = lines.FirstOrDefault(l => l.EndsWith(".ts") && !l.StartsWith("#"));
                    
                    if (!string.IsNullOrEmpty(segmentFile))
                    {
                        var segmentPath = Path.Combine(cacheDir, segmentFile);
                        if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 0)
                        {
                            return; 
                        }
                    }
                 }
                 // If process is dead and no segments, it failed.
                 throw new Exception($"FFmpeg process for {id} exited without creating valid segments.");
            }

            // Poll frequently (200ms) for responsiveness
            await Task.Delay(200);
        }

        throw new Exception("Timeout waiting for HLS segments to be created");
    }

    private void CancelExistingProcess(string id)
    {
        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var active))
            {
                _logger.LogInformation("Cancelling existing FFmpeg process for id: {Id}", id);
                active.Cts.Cancel();
                _activeProcesses.Remove(id);
            }
        }
    }

    private void RegisterActiveProcess(string id, CancellationTokenSource cts, string variantKey)
    {
        lock (_processLock)
        {
             // Cleanup old processes if limit exceeded (MAX_CONCURRENT_PROCESSES = 4)
             // This prevents CPU overload when rapidly switching tracks
            if (_activeProcesses.Count >= 4)
            {
                // Ensure we don't count the one we are about to add (though it's not in yet)
                // Find oldest process to cancel
                var oldest = _activeProcesses
                    .OrderBy(kvp => kvp.Value.StartTime)
                    .FirstOrDefault();
                
                if (!object.Equals(oldest, default(KeyValuePair<string, (CancellationTokenSource, string, DateTime)>)))
                {
                    _logger.LogInformation("Max concurrent processes limit reached. Cancelling oldest process: {Id}", oldest.Key);
                    try 
                    {
                        oldest.Value.Cts.Cancel(); 
                    } 
                    catch (ObjectDisposedException) { /* Handle race condition */ }
                    
                    _activeProcesses.Remove(oldest.Key);
                }
            }

            _activeProcesses[id] = (cts, variantKey, DateTime.Now);
            _logger.LogDebug("Registered active FFmpeg process for id: {Id}, variant: {Variant}", id, variantKey);
        }
    }

    private void UnregisterActiveProcess(string id, CancellationTokenSource cts)
    {
        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var active) && active.Cts == cts)
            {
                if (_activeProcesses.Remove(id))
                {
                    _logger.LogDebug("Unregistered FFmpeg process for id: {Id}", id);
                }
            }
        }
    }
}
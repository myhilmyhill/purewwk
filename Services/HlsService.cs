using System.Diagnostics;

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
    private readonly Dictionary<string, CancellationTokenSource> _activeProcesses = new();
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
        // 相対パス（id 先頭の / を除去）
        var variantKey = BuildVariantKey(bitRates, audioTrack); // 例: 128_default
        var key = $"{id}/{variantKey}"; // キャッシュ辞書用の内部キー

        // 同じIDに対する既存のFFmpegプロセスがあれば終了させ、不完全なキャッシュをクリア
        CancelExistingProcess(id);

        // キャッシュヒット時の完全性チェック - 完全でない場合はクリアして再生成
        if (_cacheEnabled)
        {
            var cachedEntry = await _cacheStorage.GetAsync(key);
            if (cachedEntry != null && Directory.Exists(cachedEntry.CacheDirectory))
            {
                _logger.LogDebug("Found cached HLS playlist for key: {key}, checking completeness", key);
                // キャッシュストレージが既に完全性をチェックしているので、ここまで来たら完全
                return cachedEntry.Content;
            }
        }

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

        // Create directory for HLS segments in working directory
        string workingDir = string.IsNullOrEmpty(_configuration["WorkingDirectory"]) ? AppContext.BaseDirectory : _configuration["WorkingDirectory"] ?? AppContext.BaseDirectory;
        var cacheDir = Path.Combine(workingDir, $"hls_segments{key}");
        Directory.CreateDirectory(cacheDir);
        _logger.LogDebug("HLS cache directory: {CacheDir}", cacheDir);

        var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");
        _logger.LogDebug("Using file directly for HLS processing: {FullPath}", fullPath);

        // Get base URL for segments (without filename prefix)
        var baseUrl = GetBaseUrl();
        // Log the URL generation for debugging
        _logger.LogDebug("HLS URL Generation - Original Key: {Key}", key);
        var encodedSuffix = Uri.EscapeDataString(key + "/");
        _logger.LogDebug("HLS URL Generation - Encoded Suffix: {EncodedSuffix}", encodedSuffix);
        var segmentBaseUrl = $"{baseUrl}{encodedSuffix}";
        _logger.LogDebug("HLS URL Generation - Final Base URL: {SegmentBaseUrl}", segmentBaseUrl);

        // FFmpeg command for HLS
        string ffmpegArgs;
        var seekArgs = "";
        
        if (isCueTrack)
        {
             // Use -ss before input for fast seek
             seekArgs = $"-ss {cueStart}";
             if (cueDuration > 0)
             {
                 seekArgs += $" -t {cueDuration}";
             }
        }

        // Construct command - Optimized for audio-only HLS
        var inputArgs = isCueTrack ? $"{seekArgs} -i \"{fullPath}\"" : $"-i \"{fullPath}\"";
        // Remove -hls_base_url from FFmpeg to prevent it from decoding the URL (which breaks paths with #)
        // We will inject the absolute URL manually after FFmpeg generates the playlist
        var commonHlsArgs = $"-f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\"";

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

        // Run FFmpeg in background and wait for first segment
        var cts = new CancellationTokenSource();
        RegisterActiveProcess(id, cts);
        var ffmpegTask = RunFfmpegInBackground(ffmpegArgs, cacheDir, cts.Token);
        
        // Wait for first segment to be created
        await WaitForFirstSegment(cacheDir, playlistPath);

        // Read playlist content
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        
        // Inject absolute URLs manually
        // FFmpeg generates relative paths (segment_000.ts)
        // We replace them with absolute URLs containing the correctly encoded key
        playlistContent = playlistContent.Replace("segment_", $"{segmentBaseUrl}segment_");
        
        _logger.LogDebug("Base URL: {BaseUrl}", baseUrl);
        _logger.LogDebug("Segment Base URL: {SegmentBaseUrl}", segmentBaseUrl);
        _logger.LogDebug("Playlist content with injected URLs: {PlaylistPreview}", playlistContent.Substring(0, Math.Min(200, playlistContent.Length)));

        // Continue FFmpeg processing in background (don't await)
        _ = Task.Run(async () =>
        {
            try
            {
                await ffmpegTask;
                _logger.LogInformation("FFmpeg background conversion completed");
                UnregisterActiveProcess(id);
                
                // Update cache with final playlist after completion
                if (_cacheEnabled && File.Exists(playlistPath))
                {
                    var finalPlaylistContent = await File.ReadAllTextAsync(playlistPath);
                    
                    // Manually inject absolute URLs again for the final playlist
                    finalPlaylistContent = finalPlaylistContent.Replace("segment_", $"{segmentBaseUrl}segment_");
                    
                    var finalCacheEntry = new HlsCacheEntry
                    {
                        Content = finalPlaylistContent,
                        CacheDirectory = cacheDir,
                        CreatedAt = DateTime.Now,
                        LastAccessed = DateTime.Now
                    };

                    await _cacheStorage.SetAsync(key, finalCacheEntry);
                    _logger.LogDebug("Updated final HLS cache after FFmpeg completion");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FFmpeg background conversion failed: {Message}", ex.Message);
            }
        });

        // キャッシュが有効な場合はキャッシュに保存
        if (_cacheEnabled)
        {
            var cacheEntry = new HlsCacheEntry
            {
                Content = playlistContent,
                CacheDirectory = cacheDir,
                CreatedAt = DateTime.Now,
                LastAccessed = DateTime.Now
            };

            await _cacheStorage.SetAsync(key, cacheEntry);
            _logger.LogDebug("Stored HLS playlist in cache with key: {Key}", key);
        }

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

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

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
                        process.Kill();
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("FFmpeg background process timed out, killing...");
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                    throw new Exception("FFmpeg background process timed out after 10 minutes");
                }
            }

            _logger.LogDebug("FFmpeg background process finished with exit code: {ExitCode}", process.ExitCode);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                var output = await process.StandardOutput.ReadToEndAsync();
                _logger.LogError("FFmpeg background stderr: {Error}", error);
                _logger.LogError("FFmpeg background stdout: {Output}", output);
                // Don't throw exception for background process - just log the error
                _logger.LogError("FFmpeg background error (exit code {ExitCode}): {Error}", process.ExitCode, error);
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
    }

    private async Task WaitForFirstSegment(string cacheDir, string playlistPath)
    {
        _logger.LogDebug("Waiting for first HLS segment in directory: {CacheDir}", cacheDir);
        
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
                    // Check if playlist contains at least one segment reference
                    if (playlistContent.Contains("segment_000.ts"))
                    {
                        // Check if the first segment file actually exists
                        var firstSegmentPath = Path.Combine(cacheDir, "segment_000.ts");
                        if (File.Exists(firstSegmentPath) && new FileInfo(firstSegmentPath).Length > 0)
                        {
                            _logger.LogDebug("First segment created: {FirstSegmentPath}", firstSegmentPath);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading playlist while waiting: {Message}", ex.Message);
                }
            }

            // Wait a bit before checking again
            await Task.Delay(500);
        }

        throw new Exception("Timeout waiting for first HLS segment to be created");
    }

    private void CancelExistingProcess(string id)
    {
        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var existingCts))
            {
                _logger.LogInformation("Cancelling existing FFmpeg process for id: {Id}", id);
                existingCts.Cancel();
                _activeProcesses.Remove(id);
            }
        }
    }

    private void RegisterActiveProcess(string id, CancellationTokenSource cts)
    {
        lock (_processLock)
        {
            _activeProcesses[id] = cts;
            _logger.LogDebug("Registered active FFmpeg process for id: {Id}", id);
        }
    }

    private void UnregisterActiveProcess(string id)
    {
        lock (_processLock)
        {
            if (_activeProcesses.Remove(id))
            {
                _logger.LogDebug("Unregistered FFmpeg process for id: {Id}", id);
            }
        }
    }
}
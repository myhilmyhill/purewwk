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

        // Fix directory path handling - use Unix-style paths for consistency with Lucene index
        var directoryName = id.Contains('/') ? string.Join('/', id.Split('/').SkipLast(1)) : "/";
        _logger.LogDebug("Directory name: {DirectoryName}", directoryName);

        var children = _luceneService.GetChildren(directoryName);
        _logger.LogDebug("Found {Count} children in directory", children.Count());

        var fileDoc = children.FirstOrDefault(c => c["id"] == id && c["isDir"] == "false");
        _logger.LogDebug("Found file doc: {FileDocFound}", fileDoc != null);

        if (fileDoc == null)
        {
            // デバッグ: 利用可能なファイルをすべて表示
            foreach (var child in children.Where(c => c["isDir"] == "false"))
            {
                _logger.LogDebug("Available file: id='{Id}', title='{Title}'", child["id"], child["title"]);
            }
            throw new FileNotFoundException("Media file not found: " + id);
        }
        var fullPath = fileDoc["path"];

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
        var segmentBaseUrl = $"{baseUrl}{key}/";
        
        // FFmpeg command for HLS - use first bitrate if provided, otherwise use default
        string ffmpegArgs;
        if (bitRates.Length > 0)
        {
            var bitRate = bitRates[0]; // Use only the first bitrate
            ffmpegArgs = $"-y -v error -i \"{fullPath}\" -c:v libx264 -b:v {bitRate}k -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_base_url \"{segmentBaseUrl}\" -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            _logger.LogDebug("Using single bitrate: {BitRate}k", bitRate);
        }
        else
        {
            ffmpegArgs = $"-y -v error -i \"{fullPath}\" -c:v libx264 -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_base_url \"{segmentBaseUrl}\" -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            _logger.LogDebug("Using default bitrate");
        }

        _logger.LogDebug("FFmpeg HLS command: ffmpeg {FfmpegArgs}", ffmpegArgs);

        // Run FFmpeg in background and wait for first segment
        var cts = new CancellationTokenSource();
        RegisterActiveProcess(id, cts);
        var ffmpegTask = RunFfmpegInBackground(ffmpegArgs, cacheDir, cts.Token);
        
        // Wait for first segment to be created
        await WaitForFirstSegment(cacheDir, playlistPath);

        // Read playlist content (URLs already generated by FFmpeg with hls_base_url)
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        
        _logger.LogDebug("Base URL: {BaseUrl}", baseUrl);
        _logger.LogDebug("Segment Base URL: {SegmentBaseUrl}", segmentBaseUrl);
        _logger.LogDebug("Playlist content with FFmpeg-generated URLs: {PlaylistPreview}", playlistContent.Substring(0, Math.Min(200, playlistContent.Length)));

        // Continue FFmpeg processing in background (don't await)
        _ = Task.Run(async () =>
        {
            try
            {
                await ffmpegTask;
                _logger.LogInformation("FFmpeg background conversion completed");
                UnregisterActiveProcess(id);
                
                // Update cache with final playlist after completion (URLs already generated by FFmpeg)
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
            var scheme = request.Scheme; // http or https
            var host = request.Host.Value; // localhost:5095 or yourdomain.com
            var pathBase = request.PathBase.Value; // for apps deployed in subdirectories

            var baseUrl = $"{scheme}://{host}{pathBase}/hls?key=";
            _logger.LogDebug("Auto-detected base URL: {BaseUrl}", baseUrl);
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
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using System.Net.Sockets;

namespace repos.Services;

public class HlsService
{
    private readonly ILogger<HlsService> _logger;
    private readonly IConfiguration _configuration;
    private readonly LuceneService _luceneService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHlsCacheStorage _cacheStorage;
    private readonly bool _cacheEnabled;
    private readonly bool _losslessEncodingEnabled;
    private readonly string[] _losslessFormats;

    public HlsService(ILogger<HlsService> logger, IConfiguration configuration, LuceneService luceneService, IHttpContextAccessor httpContextAccessor, IHlsCacheStorage cacheStorage)
    {
        _logger = logger;
        _configuration = configuration;
        _luceneService = luceneService;
        _httpContextAccessor = httpContextAccessor;
        _cacheStorage = cacheStorage;
        _cacheEnabled = configuration.GetValue<bool>("HlsCache:Enabled", true);
        _losslessEncodingEnabled = configuration.GetValue<bool>("LosslessEncoding:Enabled", true);
        _losslessFormats = configuration.GetSection("LosslessEncoding:LosslessFormats").Get<string[]>()
            ?? new[] { "flac", "wav", "ape", "wv", "alac", "aiff", "au" };
    }

    public async Task<string> GenerateHlsPlaylist(string id, int[] bitRates, string? audioTrack)
    {
        // 相対パス（id 先頭の / を除去）
        var variantKey = BuildVariantKey(bitRates, audioTrack); // 例: 128_default
        var key = $"{id}/{variantKey}"; // キャッシュ辞書用の内部キー

        // キャッシュヒット時はそのまま返す
        if (_cacheEnabled)
        {
            var cachedEntry = await _cacheStorage.GetAsync(key);
            if (cachedEntry != null && Directory.Exists(cachedEntry.CacheDirectory))
            {
                _logger.LogDebug("Returning cached HLS playlist for key: {key}", key);
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

        // すべてのファイルを直接処理（ロスレス変換をスキップ）
        string inputFileForHls = fullPath;
        _logger.LogDebug("Using file directly for HLS processing: {FullPath}", fullPath);

        // FFmpeg command for HLS - use first bitrate if provided, otherwise use default
        string ffmpegArgs;
        if (bitRates.Length > 0)
        {
            var bitRate = bitRates[0]; // Use only the first bitrate
            ffmpegArgs = $"-y -v error -i \"{inputFileForHls}\" -c:v libx264 -b:v {bitRate}k -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            _logger.LogDebug("Using single bitrate: {BitRate}k", bitRate);
        }
        else
        {
            ffmpegArgs = $"-y -v error -i \"{inputFileForHls}\" -c:v libx264 -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            _logger.LogDebug("Using default bitrate");
        }

        _logger.LogDebug("FFmpeg HLS command: ffmpeg {FfmpegArgs}", ffmpegArgs);

        // Run FFmpeg in background and wait for first segment
        var ffmpegTask = RunFfmpegInBackground(ffmpegArgs, cacheDir);
        
        // Wait for first segment to be created
        await WaitForFirstSegment(cacheDir, playlistPath);

        // Read and update playlist content
        // 新方式: /rest/hls/path/<relativeId>/<variantKey>/segment_XXX.ts という URL に書き換え
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        var baseUrl = GetBaseUrl();
        var fullSegmentUrl = $"{baseUrl}{key}/segment_";
        
        _logger.LogDebug("Base URL: {BaseUrl}", baseUrl);
        _logger.LogDebug("Full Segment URL prefix: {FullSegmentUrl}", fullSegmentUrl);
        _logger.LogDebug("Original playlist content preview: {PlaylistPreview}", playlistContent.Substring(0, Math.Min(200, playlistContent.Length)));
        
        // Replace segment references with full URLs (in memory only, don't write back to file)
        playlistContent = playlistContent.Replace("segment_", fullSegmentUrl);
        
        _logger.LogDebug("Updated playlist content preview: {PlaylistPreview}", playlistContent.Substring(0, Math.Min(200, playlistContent.Length)));

        // Continue FFmpeg processing in background (don't await)
        _ = Task.Run(async () =>
        {
            try
            {
                await ffmpegTask;
                _logger.LogInformation("FFmpeg background conversion completed");
                
                // Update cache with final playlist after completion
                if (_cacheEnabled && File.Exists(playlistPath))
                {
                    var finalPlaylistContent = await File.ReadAllTextAsync(playlistPath);
                    // Replace segment references with full URLs for cache (in memory only)
                    var finalPlaylistWithUrls = finalPlaylistContent.Replace("segment_", fullSegmentUrl);
                    
                    var finalCacheEntry = new HlsCacheEntry
                    {
                        Content = finalPlaylistWithUrls,
                        CacheDirectory = cacheDir,
                        CreatedAt = DateTime.Now,
                        LastAccessed = DateTime.Now
                    };

                    await _cacheStorage.SetAsync(id, finalCacheEntry);
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

            await _cacheStorage.SetAsync(id, cacheEntry);
            _logger.LogDebug("Stored HLS playlist in cache with key: {Id}", id);
        }

        return playlistContent;
    }

    private string BuildVariantKey(int[] bitRates, string? audioTrack)
    {
        var bitRateStr = bitRates.Length > 0 ? bitRates[0].ToString() : "default"; // 単一ビットレート前提
        var audioTrackStr = audioTrack ?? "default";
        return $"{bitRateStr}_{audioTrackStr}";
    }

    private string NormalizeRelativePath(string relativeId)
    {
        // Windows 不正文字や .. の混入を防ぐ最小実装（既に id は内部管理で信頼されている前提）
        // ここではシンプルにそのまま返すが、セキュリティ強化余地あり
        return relativeId.Replace('\\', '/');
    }

    private string EscapeUrlPath(string relativeId)
    {
        // ブラウザ URL 用に各パスセグメントをエスケープ
        return string.Join('/', relativeId.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
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

    private bool IsLosslessFormat(string filePath)
    {
        // すべてのファイルを直接処理するため、常にfalseを返す
        return false;
    }

    private async Task<string> EncodeToLossyFormat(string inputPath, string cacheDir)
    {
        var targetFormat = _configuration.GetValue<string>("LosslessEncoding:TargetFormat", "m4a");
        var audioCodec = _configuration.GetValue<string>("LosslessEncoding:AudioCodec", "aac");
        var audioBitrate = _configuration.GetValue<string>("LosslessEncoding:AudioBitrate", "320k");
        var sampleRateConfig = _configuration.GetValue<string>("LosslessEncoding:AudioSampleRate");

        var encodedFileName = $"encoded_{Path.GetFileNameWithoutExtension(inputPath)}.{targetFormat}";
        var encodedPath = Path.Combine(cacheDir, encodedFileName);

        // エンコード済みファイルが既に存在する場合はそれを使用
        if (File.Exists(encodedPath))
        {
            _logger.LogDebug("Using existing encoded file: {EncodedPath}", encodedPath);
            return encodedPath;
        }

        _logger.LogInformation("Encoding lossless file to {TargetFormat}: {InputPath} -> {EncodedPath}", targetFormat, inputPath, encodedPath);

        // サンプルレートの処理：nullの場合は元ファイルのサンプルレートを維持（-arオプションを省略）
        string sampleRateArgs = "";
        if (!string.IsNullOrEmpty(sampleRateConfig))
        {
            sampleRateArgs = $" -ar {sampleRateConfig}";
            _logger.LogDebug("Using configured sample rate: {SampleRate}", sampleRateConfig);
        }
        else
        {
            _logger.LogDebug("Sample rate not specified - maintaining original sample rate");
        }

        var ffmpegArgs = $"-y -v error -i \"{inputPath}\" -c:a {audioCodec} -b:a {audioBitrate}{sampleRateArgs} \"{encodedPath}\"";

        _logger.LogDebug("Encoding command: ffmpeg {FfmpegArgs}", ffmpegArgs);

        await RunFfmpeg(ffmpegArgs);

        if (!File.Exists(encodedPath))
        {
            throw new Exception($"Failed to encode lossless file: {inputPath}");
        }

        _logger.LogInformation("Successfully encoded lossless file: {EncodedPath}", encodedPath);
        return encodedPath;
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



    private async Task RunFfmpegInBackground(string args, string cacheDir)
    {
        try
        {
            // Get FFmpeg path from environment variable or configuration, fallback to "ffmpeg"
            var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                ?? _configuration["FFmpeg:Path"]
                ?? "ffmpeg";

            _logger.LogDebug("Starting FFmpeg in background from path: {FfmpegPath}", ffmpegPath);
            _logger.LogDebug("FFmpeg args: {Args}", args);

            var process = new Process
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
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("FFmpeg background process timed out, killing...");
                    process.Kill();
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



    private async Task RunFfmpeg(string args)
    {
        try
        {
            // Get FFmpeg path from environment variable or configuration, fallback to "ffmpeg"
            var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                ?? _configuration["FFmpeg:Path"]
                ?? "ffmpeg";

            _logger.LogDebug("Starting FFmpeg from path: {FfmpegPath}", ffmpegPath);
            _logger.LogDebug("FFmpeg args: {Args}", args);

            var process = new Process
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

            // タイムアウトを30秒に設定
            var timeout = TimeSpan.FromSeconds(30);
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("FFmpeg process timed out, killing...");
                    process.Kill();
                    throw new Exception("FFmpeg process timed out after 30 seconds");
                }
            }

            _logger.LogDebug("FFmpeg finished with exit code: {ExitCode}", process.ExitCode);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                var output = await process.StandardOutput.ReadToEndAsync();
                _logger.LogError("FFmpeg stderr: {Error}", error);
                _logger.LogError("FFmpeg stdout: {Output}", output);
                throw new Exception($"FFmpeg error (exit code {process.ExitCode}): {error}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new Exception("FFmpeg is not installed or not found in PATH. Please install FFmpeg to use HLS streaming functionality.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running FFmpeg: {Message}", ex.Message);
            throw new Exception($"Error running FFmpeg: {ex.Message}");
        }
    }
}
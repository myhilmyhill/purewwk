using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;

namespace repos.Services;

public class HlsService
{
    private readonly IConfiguration _configuration;
    private readonly LuceneService _luceneService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHlsCacheStorage _cacheStorage;
    private readonly bool _cacheEnabled;
    private readonly bool _losslessEncodingEnabled;
    private readonly string[] _losslessFormats;

    public HlsService(IConfiguration configuration, LuceneService luceneService, IHttpContextAccessor httpContextAccessor, IHlsCacheStorage cacheStorage)
    {
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
        var relativeId = id.TrimStart('/');
        var variantKey = BuildVariantKey(bitRates, audioTrack); // 例: 128_default
        var hierarchicalKey = $"{relativeId}|{variantKey}"; // キャッシュ辞書用の内部キー

        // キャッシュヒット時はそのまま返す
        if (_cacheEnabled)
        {
            var cachedEntry = await _cacheStorage.GetAsync(hierarchicalKey);
            if (cachedEntry != null && Directory.Exists(cachedEntry.CacheDirectory))
            {
                Console.WriteLine($"Returning cached HLS playlist for hierarchical key: {hierarchicalKey}");
                return cachedEntry.Content;
            }
        }

        // Get file path from Lucene index
        Console.WriteLine($"Looking for file with id: {id}");

        // Fix directory path handling - use Unix-style paths for consistency with Lucene index
        var directoryName = "/";
        if (id.Contains("/") && id.LastIndexOf("/") > 0)
        {
            directoryName = id.Substring(0, id.LastIndexOf("/"));
        }
        Console.WriteLine($"Directory name: {directoryName}");

        var children = _luceneService.GetChildren(directoryName);
        Console.WriteLine($"Found {children.Count()} children in directory");

        var fileDoc = children.FirstOrDefault(c => c["id"] == id && c["isDir"] == "false");
        Console.WriteLine($"Found file doc: {fileDoc != null}");

        if (fileDoc == null)
        {
            // デバッグ: 利用可能なファイルをすべて表示
            foreach (var child in children.Where(c => c["isDir"] == "false"))
            {
                Console.WriteLine($"Available file: id='{child["id"]}', title='{child["title"]}'");
            }
            throw new FileNotFoundException("Media file not found");
        }
        var fullPath = fileDoc["path"];

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Media file not found on disk");
        }

        // Create directory for HLS segments in working directory
        var workingDir = _configuration["WorkingDirectory"];
        var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
        // ディレクトリ構造: hls_segments/<relativeId>/<variantKey>/
        var cacheDir = Path.Combine(baseDir, "hls_segments", NormalizeRelativePath(relativeId), variantKey);
        Directory.CreateDirectory(cacheDir);

        var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");

        // Get base URL from current request
        var baseUrl = GetBaseUrl();

        // Clean up the id to avoid double slashes
        var cleanId = id.TrimStart('/');


        
        // すべてのファイルを直接処理（ロスレス変換をスキップ）
        string inputFileForHls = fullPath;
        Console.WriteLine($"Using file directly for HLS processing: {fullPath}");

        // FFmpeg command for HLS - use first bitrate if provided, otherwise use default
        string ffmpegArgs;
        if (bitRates.Length > 0)
        {
            var bitRate = bitRates[0]; // Use only the first bitrate
            ffmpegArgs = $"-y -v error -i \"{inputFileForHls}\" -c:v libx264 -b:v {bitRate}k -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            Console.WriteLine($"Using single bitrate: {bitRate}k");
        }
        else
        {
            ffmpegArgs = $"-y -v error -i \"{inputFileForHls}\" -c:v libx264 -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            Console.WriteLine("Using default bitrate");
        }

        Console.WriteLine($"FFmpeg HLS command: ffmpeg {ffmpegArgs}");

        // Run FFmpeg in background and wait for first segment
        var ffmpegTask = RunFfmpegInBackground(ffmpegArgs, cacheDir);
        
        // Wait for first segment to be created
        await WaitForFirstSegment(cacheDir, playlistPath);

        // Read and update playlist content
        // 新方式: /rest/hls/path/<relativeId>/<variantKey>/segment_XXX.ts という URL に書き換え
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        var urlRelativePath = $"path/{EscapeUrlPath(relativeId)}/{variantKey}"; // 実際の HTTP パス（/rest/hls/ の後ろ）
        playlistContent = playlistContent.Replace("segment_", $"{baseUrl}{urlRelativePath}/segment_");
        await File.WriteAllTextAsync(playlistPath, playlistContent);

        // Continue FFmpeg processing in background (don't await)
        _ = Task.Run(async () =>
        {
            try
            {
                await ffmpegTask;
                Console.WriteLine("FFmpeg background conversion completed");
                
                // Final playlist update after completion
                if (File.Exists(playlistPath))
                {
                    var finalPlaylistContent = await File.ReadAllTextAsync(playlistPath);
                    if (!finalPlaylistContent.Contains(baseUrl))
                    {
                        finalPlaylistContent = finalPlaylistContent.Replace("segment_", $"{baseUrl}{urlRelativePath}/segment_");
                        await File.WriteAllTextAsync(playlistPath, finalPlaylistContent);
                    }

                    // Update final cache
                    if (_cacheEnabled)
                    {
                        var finalCacheEntry = new HlsCacheEntry
                        {
                            Content = finalPlaylistContent,
                            CacheDirectory = cacheDir,
                            CreatedAt = DateTime.Now,
                            LastAccessed = DateTime.Now
                        };

                        await _cacheStorage.SetAsync(hierarchicalKey, finalCacheEntry);
                        Console.WriteLine("Updated final HLS cache after completion");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FFmpeg background conversion failed: {ex.Message}");
            }
        });

        // Read playlist
        playlistContent = await File.ReadAllTextAsync(playlistPath);

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

            await _cacheStorage.SetAsync(hierarchicalKey, cacheEntry);
            Console.WriteLine($"Stored HLS playlist in cache with key: {hierarchicalKey}");
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
                    Console.WriteLine($"Error in HLS cache cleanup: {ex.Message}");
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
            Console.WriteLine($"Using existing encoded file: {encodedPath}");
            return encodedPath;
        }

        Console.WriteLine($"Encoding lossless file to {targetFormat}: {inputPath} -> {encodedPath}");

        // サンプルレートの処理：nullの場合は元ファイルのサンプルレートを維持（-arオプションを省略）
        string sampleRateArgs = "";
        if (!string.IsNullOrEmpty(sampleRateConfig))
        {
            sampleRateArgs = $" -ar {sampleRateConfig}";
            Console.WriteLine($"Using configured sample rate: {sampleRateConfig}");
        }
        else
        {
            Console.WriteLine("Sample rate not specified - maintaining original sample rate");
        }

        var ffmpegArgs = $"-y -v error -i \"{inputPath}\" -c:a {audioCodec} -b:a {audioBitrate}{sampleRateArgs} \"{encodedPath}\"";

        Console.WriteLine($"Encoding command: ffmpeg {ffmpegArgs}");

        await RunFfmpeg(ffmpegArgs);

        if (!File.Exists(encodedPath))
        {
            throw new Exception($"Failed to encode lossless file: {inputPath}");
        }

        Console.WriteLine($"Successfully encoded lossless file: {encodedPath}");
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

            var baseUrl = $"{scheme}://{host}{pathBase}/rest/hls/";
            Console.WriteLine($"Auto-detected base URL: {baseUrl}");
            return baseUrl;
        }

        // Fallback to configuration if HttpContext is not available
        var configUrl = _configuration["Hls:BaseUrl"];
        if (!string.IsNullOrEmpty(configUrl))
        {
            Console.WriteLine($"Using configured base URL: {configUrl}");
            return configUrl;
        }

        // Final fallback
        var fallbackUrl = "http://localhost:5095/rest/hls/";
        Console.WriteLine($"Using fallback base URL: {fallbackUrl}");
        return fallbackUrl;
    }



    private async Task RunFfmpegInBackground(string args, string cacheDir)
    {
        try
        {
            // Get FFmpeg path from environment variable or configuration, fallback to "ffmpeg"
            var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                ?? _configuration["FFmpeg:Path"]
                ?? "ffmpeg";

            Console.WriteLine($"Starting FFmpeg in background from path: {ffmpegPath}");
            Console.WriteLine($"FFmpeg args: {args}");

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
                    Console.WriteLine("FFmpeg background process timed out, killing...");
                    process.Kill();
                    throw new Exception("FFmpeg background process timed out after 10 minutes");
                }
            }

            Console.WriteLine($"FFmpeg background process finished with exit code: {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                var output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine($"FFmpeg background stderr: {error}");
                Console.WriteLine($"FFmpeg background stdout: {output}");
                // Don't throw exception for background process - just log the error
                Console.WriteLine($"FFmpeg background error (exit code {process.ExitCode}): {error}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            Console.WriteLine("FFmpeg is not installed or not found in PATH for background processing.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running FFmpeg in background: {ex.Message}");
        }
    }

    private async Task WaitForFirstSegment(string cacheDir, string playlistPath)
    {
        Console.WriteLine($"Waiting for first HLS segment in directory: {cacheDir}");
        
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
                            Console.WriteLine($"First segment created: {firstSegmentPath}");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading playlist while waiting: {ex.Message}");
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

            Console.WriteLine($"Starting FFmpeg from path: {ffmpegPath}");
            Console.WriteLine($"FFmpeg args: {args}");

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
                    Console.WriteLine("FFmpeg process timed out, killing...");
                    process.Kill();
                    throw new Exception("FFmpeg process timed out after 30 seconds");
                }
            }

            Console.WriteLine($"FFmpeg finished with exit code: {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                var output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine($"FFmpeg stderr: {error}");
                Console.WriteLine($"FFmpeg stdout: {output}");
                throw new Exception($"FFmpeg error (exit code {process.ExitCode}): {error}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new Exception("FFmpeg is not installed or not found in PATH. Please install FFmpeg to use HLS streaming functionality.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running FFmpeg: {ex.Message}");
            throw new Exception($"Error running FFmpeg: {ex.Message}");
        }
    }
}
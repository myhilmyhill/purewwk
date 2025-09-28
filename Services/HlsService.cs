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

    public HlsService(IConfiguration configuration, LuceneService luceneService, IHttpContextAccessor httpContextAccessor, IHlsCacheStorage cacheStorage)
    {
        _configuration = configuration;
        _luceneService = luceneService;
        _httpContextAccessor = httpContextAccessor;
        _cacheStorage = cacheStorage;
        _cacheEnabled = configuration.GetValue<bool>("HlsCache:Enabled", true);
    }

    public async Task<string> GenerateHlsPlaylist(string id, int[] bitRates, string? audioTrack)
    {
        // キャッシュキーを生成
        var cacheKey = GenerateCacheKey(id, bitRates, audioTrack);
        
        // キャッシュが有効でヒットした場合はキャッシュから返す
        if (_cacheEnabled)
        {
            var cachedEntry = await _cacheStorage.GetAsync(cacheKey);
            if (cachedEntry != null && Directory.Exists(cachedEntry.CacheDirectory))
            {
                Console.WriteLine($"Returning cached HLS playlist for key: {cacheKey}");
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
        var cacheDir = Path.Combine(baseDir, "hls_segments", cacheKey.Replace("/", "_").Replace("\\", "_"));
        Directory.CreateDirectory(cacheDir);

        var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");

        // Get base URL from current request
        var baseUrl = GetBaseUrl();
        
        // Clean up the id to avoid double slashes
        var cleanId = id.TrimStart('/');

        // FFmpeg command for HLS - use first bitrate if provided, otherwise use default
        string ffmpegArgs;
        if (bitRates.Length > 0)
        {
            var bitRate = bitRates[0]; // Use only the first bitrate
            ffmpegArgs = $"-y -v error -i \"{fullPath}\" -c:v libx264 -b:v {bitRate}k -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            Console.WriteLine($"Using single bitrate: {bitRate}k");
        }
        else
        {
            ffmpegArgs = $"-y -v error -i \"{fullPath}\" -c:v libx264 -c:a aac -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            Console.WriteLine("Using default bitrate");
        }

        Console.WriteLine($"FFmpeg command: ffmpeg {ffmpegArgs}");

        // Run FFmpeg
        await RunFfmpeg(ffmpegArgs);
        
        // Read and update playlist content
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        playlistContent = playlistContent.Replace("segment_", $"{baseUrl}{cleanId}/segment_");
        await File.WriteAllTextAsync(playlistPath, playlistContent);

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
            
            await _cacheStorage.SetAsync(cacheKey, cacheEntry);
            Console.WriteLine($"Stored HLS playlist in cache with key: {cacheKey}");
        }

        return playlistContent;
    }

    private string GenerateCacheKey(string id, int[] bitRates, string? audioTrack)
    {
        var bitRateStr = bitRates.Length > 0 ? string.Join(",", bitRates) : "default";
        var audioTrackStr = audioTrack ?? "default";
        return $"{id}_{bitRateStr}_{audioTrackStr}".Replace("/", "_").Replace("\\", "_");
    }

    public async Task StartCacheCleanupAsync()
    {
        if (!_cacheEnabled) return;

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
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

        // Validate and prepare input file
        Console.WriteLine($"Validating input file: {fullPath}");
        var fileInfo = new FileInfo(fullPath);
        Console.WriteLine($"File size: {fileInfo.Length} bytes");
        Console.WriteLine($"File extension: {fileInfo.Extension}");
        
        // Validate file accessibility
        if (!await ValidateFileAccess(fullPath))
        {
            throw new Exception($"Cannot access input file: {fullPath}");
        }
        
        // シンプルな処理：すべてのファイルを直接処理
        string inputFileForHls = fullPath;
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        
        // MP4/M4A ファイルのみ moov atom の問題をチェック
        if (new[] { ".mp4", ".m4a", ".m4v", ".mov" }.Contains(extension))
        {
            Console.WriteLine($"Checking MP4 file for moov atom issues: {fullPath}");
            inputFileForHls = await ValidateAndFixMp4File(fullPath, cacheDir);
        }
        else
        {
            Console.WriteLine($"Using file directly for HLS processing: {fullPath}");
        }

        // シンプルな FFmpeg コマンド - すべての音楽ファイルを同じように処理
        string ffmpegArgs;
        if (bitRates.Length > 0)
        {
            var bitRate = bitRates[0]; // Use only the first bitrate
            ffmpegArgs = $"-y -i \"{inputFileForHls}\" -c:a aac -b:a {bitRate}k -vn -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            Console.WriteLine($"Using audio bitrate: {bitRate}k");
        }
        else
        {
            ffmpegArgs = $"-y -i \"{inputFileForHls}\" -c:a aac -b:a 128k -vn -f hls -hls_time 10 -hls_list_size 0 -hls_segment_filename \"{cacheDir}/segment_%03d.ts\" \"{playlistPath}\"";
            Console.WriteLine("Using default audio bitrate: 128k");
        }

        Console.WriteLine($"FFmpeg HLS command: ffmpeg {ffmpegArgs}");

        // Run FFmpeg
        await RunFfmpeg(ffmpegArgs);

        // Read and update playlist content
        // 新方式: /rest/hls/path/<relativeId>/<variantKey>/segment_XXX.ts という URL に書き換え
        var playlistContent = await File.ReadAllTextAsync(playlistPath);
        var urlRelativePath = $"path/{EscapeUrlPath(relativeId)}/{variantKey}"; // 実際の HTTP パス（/rest/hls/ の後ろ）
        playlistContent = playlistContent.Replace("segment_", $"{baseUrl}{urlRelativePath}/segment_");
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
        if (!_losslessEncodingEnabled)
            return false;

        var extension = Path.GetExtension(filePath)?.ToLowerInvariant()?.TrimStart('.');
        return !string.IsNullOrEmpty(extension) && _losslessFormats.Contains(extension);
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

        // First, validate the lossless file before encoding
        var extension = Path.GetExtension(inputPath).ToLowerInvariant();
        Console.WriteLine($"Validating {extension} file before encoding...");
        
        var probeResult = await ProbeMediaFile(inputPath);
        if (!probeResult.IsValid)
        {
            Console.WriteLine($"Lossless file validation failed: {probeResult.Error}");
            
            // For FLAC files, try to recover using different FFmpeg options
            if (extension == ".flac")
            {
                Console.WriteLine("Attempting FLAC-specific recovery encoding...");
                return await EncodeFlacWithRecovery(inputPath, encodedPath, audioCodec ?? "aac", audioBitrate ?? "128k", sampleRateConfig);
            }
            
            Console.WriteLine("Attempting standard encoding with error recovery...");
        }

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

        // Use more robust encoding options for potentially corrupted files
        var ffmpegArgs = $"-y -v info -analyzeduration 10M -probesize 10M -err_detect ignore_err -i \"{inputPath}\" -c:a {audioCodec} -b:a {audioBitrate}{sampleRateArgs} \"{encodedPath}\"";

        Console.WriteLine($"Encoding command: ffmpeg {ffmpegArgs}");

        try
        {
            await RunFfmpeg(ffmpegArgs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Standard encoding failed: {ex.Message}");
            
            // Try fallback encoding with more aggressive error handling
            if (extension == ".flac")
            {
                Console.WriteLine("Attempting FLAC fallback encoding...");
                return await EncodeFlacWithRecovery(inputPath, encodedPath, audioCodec ?? "aac", audioBitrate ?? "128k", sampleRateConfig);
            }
            
            throw;
        }

        if (!File.Exists(encodedPath))
        {
            throw new Exception($"Failed to encode lossless file: {inputPath}");
        }

        Console.WriteLine($"Successfully encoded lossless file: {encodedPath}");
        return encodedPath;
    }

    private async Task<string> EncodeFlacWithRecovery(string inputPath, string encodedPath, string audioCodec, string audioBitrate, string? sampleRateConfig)
    {
        Console.WriteLine("Attempting FLAC recovery encoding with aggressive error handling...");

        // Ensure audioCodec and audioBitrate are not null
        audioCodec = audioCodec ?? "aac";
        audioBitrate = audioBitrate ?? "128k";

        string sampleRateArgs = "";
        if (!string.IsNullOrEmpty(sampleRateConfig))
        {
            sampleRateArgs = $" -ar {sampleRateConfig}";
        }

        // Try multiple encoding strategies for corrupted FLAC files
        var strategies = new[]
        {
            // Strategy 1: Ignore all errors and use shorter analysis
            $"-y -v warning -analyzeduration 1M -probesize 1M -err_detect ignore_err -fflags +discardcorrupt -i \"{inputPath}\" -c:a {audioCodec} -b:a {audioBitrate}{sampleRateArgs} \"{encodedPath}\"",
            
            // Strategy 2: Use raw audio format assumptions
            $"-y -v warning -f flac -err_detect ignore_err -i \"{inputPath}\" -c:a {audioCodec} -b:a {audioBitrate}{sampleRateArgs} \"{encodedPath}\"",
            
            // Strategy 3: Very basic conversion with maximum error tolerance
            $"-y -v warning -err_detect ignore_err -fflags +discardcorrupt +igndts -avoid_negative_ts make_zero -i \"{inputPath}\" -c:a {audioCodec} -b:a {audioBitrate} -ac 2{sampleRateArgs} \"{encodedPath}\""
        };

        Exception? lastException = null;

        for (int i = 0; i < strategies.Length; i++)
        {
            try
            {
                Console.WriteLine($"Trying FLAC recovery strategy {i + 1}: {strategies[i]}");
                
                // Delete any partial output from previous attempts
                if (File.Exists(encodedPath))
                {
                    File.Delete(encodedPath);
                }

                await RunFfmpegCommand(strategies[i], 120); // Extended timeout for recovery

                if (File.Exists(encodedPath) && new FileInfo(encodedPath).Length > 0)
                {
                    Console.WriteLine($"FLAC recovery strategy {i + 1} succeeded!");
                    return encodedPath;
                }
                else
                {
                    Console.WriteLine($"FLAC recovery strategy {i + 1} produced no output");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FLAC recovery strategy {i + 1} failed: {ex.Message}");
                lastException = ex;
            }
        }

        // If all strategies fail, throw the last exception
        throw new Exception($"All FLAC recovery strategies failed. File may be severely corrupted: {inputPath}", lastException);
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

    private async Task<bool> ValidateFileAccess(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var buffer = new byte[16];
            var bytesRead = await fs.ReadAsync(buffer, 0, 16);
            Console.WriteLine($"File validation: Read {bytesRead} bytes successfully");
            return bytesRead > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"File validation failed: {ex.Message}");
            return false;
        }
    }

    private async Task<string> ValidateAndFixMp4File(string inputPath, string cacheDir)
    {
        var extension = Path.GetExtension(inputPath).ToLowerInvariant();
        
        // Only process MP4 container formats that might have moov atom issues
        if (!new[] { ".mp4", ".m4a", ".m4v", ".mov" }.Contains(extension))
        {
            Console.WriteLine($"File extension {extension} - no MP4 validation needed");
            return inputPath;
        }

        Console.WriteLine($"Validating MP4 file for moov atom: {inputPath}");
        
        // First, try to probe the file with FFmpeg to detect issues
        var probeResult = await ProbeMediaFile(inputPath);
        if (probeResult.IsValid)
        {
            Console.WriteLine("MP4 file validation successful - no issues detected");
            return inputPath;
        }

        Console.WriteLine($"MP4 file has issues: {probeResult.Error}");
        
        // If moov atom is missing or at the end, try to fix it
        if (probeResult.Error.Contains("moov atom not found") || 
            probeResult.Error.Contains("Invalid data found when processing input"))
        {
            Console.WriteLine("Attempting to fix MP4 file with moov atom issues...");
            return await FixMp4MovAtom(inputPath, cacheDir);
        }

        // For other issues, return original path and let FFmpeg handle it
        Console.WriteLine("MP4 file has other issues - proceeding with original file");
        return inputPath;
    }

    private async Task<(bool IsValid, string Error)> ProbeMediaFile(string inputPath)
    {
        try
        {
            var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                ?? _configuration["FFmpeg:Path"]
                ?? "ffmpeg";

            var probeArgs = $"-v error -i \"{inputPath}\" -f null -";
            Console.WriteLine($"Probing file: ffmpeg {probeArgs}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = probeArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);

            var error = await process.StandardError.ReadToEndAsync();
            var output = await process.StandardOutput.ReadToEndAsync();
            
            Console.WriteLine($"Probe result - Exit code: {process.ExitCode}");
            if (!string.IsNullOrEmpty(error))
            {
                Console.WriteLine($"Probe stderr: {error}");
            }

            return (process.ExitCode == 0, error);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error probing media file: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private async Task<string> FixMp4MovAtom(string inputPath, string cacheDir)
    {
        try
        {
            var fixedFileName = $"fixed_{Path.GetFileName(inputPath)}";
            var fixedPath = Path.Combine(cacheDir, fixedFileName);

            // Check if fixed version already exists
            if (File.Exists(fixedPath))
            {
                Console.WriteLine($"Using existing fixed MP4 file: {fixedPath}");
                return fixedPath;
            }

            Console.WriteLine($"Fixing MP4 moov atom: {inputPath} -> {fixedPath}");

            var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                ?? _configuration["FFmpeg:Path"]
                ?? "ffmpeg";

            // Use FFmpeg to copy and fix the MP4 structure
            var fixArgs = $"-y -v info -i \"{inputPath}\" -c copy -movflags +faststart \"{fixedPath}\"";
            Console.WriteLine($"Fix command: ffmpeg {fixArgs}");

            await RunFfmpegCommand(fixArgs, 60); // Allow more time for fixing

            if (!File.Exists(fixedPath))
            {
                throw new Exception("Failed to create fixed MP4 file");
            }

            Console.WriteLine($"Successfully fixed MP4 file: {fixedPath}");
            return fixedPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to fix MP4 file: {ex.Message}");
            Console.WriteLine("Falling back to original file");
            return inputPath; // Fall back to original file
        }
    }

    private async Task RunFfmpegCommand(string args, int timeoutSeconds = 30)
    {
        var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
            ?? _configuration["FFmpeg:Path"]
            ?? "ffmpeg";

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
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"FFmpeg process timed out after {timeoutSeconds} seconds, killing...");
            try { process.Kill(); } catch { }
            throw new Exception($"FFmpeg process timed out after {timeoutSeconds} seconds");
        }

        var error = await process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        
        Console.WriteLine($"FFmpeg exit code: {process.ExitCode}");
        if (!string.IsNullOrEmpty(error))
        {
            Console.WriteLine($"FFmpeg stderr: {error}");
        }
        if (!string.IsNullOrEmpty(output))
        {
            Console.WriteLine($"FFmpeg stdout: {output}");
        }

        if (process.ExitCode != 0)
        {
            throw new Exception($"FFmpeg error (exit code {process.ExitCode}): {error}");
        }
    }

    private async Task RunFfmpeg(string args)
    {
        try
        {
            Console.WriteLine($"Running FFmpeg with args: {args}");
            await RunFfmpegCommand(args, 60); // Use 60 second timeout for HLS generation
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new Exception("FFmpeg is not installed or not found in PATH. Please install FFmpeg to use HLS streaming functionality.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running FFmpeg: {ex.Message}");
            throw;
        }
    }
}
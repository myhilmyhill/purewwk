using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PureWwk.Plugins.Hls;

public interface IHlsCacheStorage
{
    Task<HlsCacheEntry?> GetAsync(string key);
    Task SetAsync(string key, HlsCacheEntry entry);
    Task RemoveAsync(string key);
    Task CleanupExpiredEntriesAsync();
}

public class HlsCacheEntry
{
    public string Content { get; set; } = string.Empty;
    public string CacheDirectory { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessed { get; set; }
}

public class HlsCacheStorage : IHlsCacheStorage
{
    private readonly ILogger<HlsCacheStorage> _logger;
    private readonly string _hlsSegmentsDirectory;
    private readonly int _maxSize;
    private readonly TimeSpan _maxAge;

    public HlsCacheStorage(ILogger<HlsCacheStorage> logger, IConfiguration configuration, int maxSize, TimeSpan maxAge)
    {
        _logger = logger;
        _maxSize = maxSize;
        _maxAge = maxAge;
        _configuration = configuration;
        
        var workingDir = _configuration["WorkingDirectory"] ?? AppContext.BaseDirectory;
        _hlsSegmentsDirectory = Path.Combine(workingDir, "hls_segments");
        Directory.CreateDirectory(_hlsSegmentsDirectory);
    }

    private readonly IConfiguration _configuration;

    public async Task<HlsCacheEntry?> GetAsync(string key)
    {
        try
        {
            var cacheDir = Path.Combine(_hlsSegmentsDirectory, key);
            var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");

            if (!File.Exists(playlistPath))
            {
                _logger.LogDebug("HLS Cache miss for key: {Key} (file not found)", key);
                return null;
            }

            var fileInfo = new FileInfo(playlistPath);
            
            // 有効期限をチェック
            if (DateTime.Now - fileInfo.CreationTime > _maxAge)
            {
                _logger.LogDebug("HLS Cache expired for key: {Key}", key);
                await RemoveAsync(key);
                return null;
            }

            // コンテンツを読み込み
            var content = await File.ReadAllTextAsync(playlistPath);
            
            // プレイリストの完全性をチェック
            if (!IsPlaylistComplete(content, cacheDir))
            {
                _logger.LogDebug("HLS Cache incomplete for key: {Key}", key);
                // 削除はせずにnullを返す（進行中の可能性があるため）
                return null;
            }
            
            // アクセス時刻を更新
            var now = DateTime.Now;
            File.SetLastAccessTime(playlistPath, now);
            
            var entry = new HlsCacheEntry
            {
                Content = content,
                CacheDirectory = cacheDir,
                CreatedAt = fileInfo.CreationTime,
                LastAccessed = now
            };

            _logger.LogDebug("HLS Cache hit for key: {Key}", key);
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading cache for key: {Key}", key);
            return null;
        }
    }

    public async Task SetAsync(string key, HlsCacheEntry entry)
    {
        try
        {
            // ファイルベースなので特に何もしない - FFmpegが既にファイルを作成している
            // ただし、アクセス時刻を更新してキャッシュの有効性を記録
            var cacheDir = Path.Combine(_hlsSegmentsDirectory, key);
            var playlistPath = Path.Combine(cacheDir, "playlist.m3u8");
            
            if (File.Exists(playlistPath))
            {
                // ファイルの最終アクセス時刻と更新時刻を現在時刻に設定
                var now = DateTime.Now;
                File.SetLastAccessTime(playlistPath, now);
                File.SetLastWriteTime(playlistPath, now);
                _logger.LogDebug("HLS Cache stored and access time updated: {Key}", key);
            }
            else
            {
                _logger.LogDebug("HLS Cache stored (file not yet available): {Key}", key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating cache access time for key {Key}: {Message}", key, ex.Message);
        }
        
        await Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        try
        {
            var cacheDir = Path.Combine(_hlsSegmentsDirectory, key);
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
                _logger.LogDebug("HLS Cache removed: {Key}", key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing cache directory for key {Key}: {Message}", key, ex.Message);
        }
        return Task.CompletedTask;
    }

    public async Task CleanupExpiredEntriesAsync()
    {
        try
        {
            if (!Directory.Exists(_hlsSegmentsDirectory))
                return;

            _logger.LogDebug("Starting recursive HLS Cache cleanup in {Dir}", _hlsSegmentsDirectory);

            var allPlaylists = Directory.GetFiles(_hlsSegmentsDirectory, "playlist.m3u8", SearchOption.AllDirectories);
            var keysToRemove = new List<string>();
            var now = DateTime.Now;

            foreach (var playlistPath in allPlaylists)
            {
                var dir = Path.GetDirectoryName(playlistPath);
                if (dir == null) continue;

                var fileInfo = new FileInfo(playlistPath);
                var lastActiveTime = fileInfo.CreationTime > fileInfo.LastWriteTime 
                    ? fileInfo.CreationTime 
                    : fileInfo.LastWriteTime;
                
                // また、LastAccessTimeも考慮（ただしOSによっては更新されないので注意）
                if (fileInfo.LastAccessTime > lastActiveTime) lastActiveTime = fileInfo.LastAccessTime;

                if (now - lastActiveTime > _maxAge)
                {
                    // ルートディレクトリからの相対パスをキーとして使用
                    var relativePath = Path.GetRelativePath(_hlsSegmentsDirectory, dir);
                    keysToRemove.Add(relativePath);
                }
            }

            // 特殊ケース: プレイリストがないが古いディレクトリを削除（ゴミ掃除）
            // ただし、作成されてから間もない（5分以内）ものは除外する
            var allDirs = Directory.GetDirectories(_hlsSegmentsDirectory, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length) // 深いディレクトリから先にチェック
                .ToList();

            foreach (var dir in allDirs)
            {
                var playlistPath = Path.Combine(dir, "playlist.m3u8");
                if (!File.Exists(playlistPath))
                {
                    // プレイリストがなく、かつ一定時間（5分）以上経っているディレクトリのみ削除候補
                    var hasSubDirs = Directory.GetDirectories(dir).Any();
                    if (!hasSubDirs) // 子ディレクトリがある場合は、その中でプレイリストがあるかもしれないので消さない
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (now - dirInfo.LastWriteTime > TimeSpan.FromMinutes(5))
                        {
                            var relativePath = Path.GetRelativePath(_hlsSegmentsDirectory, dir);
                            keysToRemove.Add(relativePath);
                        }
                    }
                }
            }

            // サイズ制限チェックは今回は保持（キーの取得が少し複雑になるが、シンプルな実装にとどめる）
            // ...

            foreach (var key in keysToRemove.Distinct())
            {
                await RemoveAsync(key);
            }

            if (keysToRemove.Count > 0)
            {
                _logger.LogInformation("HLS Cache cleanup: removed {Count} entries", keysToRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup: {Message}", ex.Message);
        }
    }

    private bool IsPlaylistComplete(string playlistContent, string cacheDir)
    {
        try
        {
            // 基本的なM3U8形式チェック
            if (!playlistContent.Contains("#EXTM3U") || !playlistContent.Contains("#EXT-X-VERSION"))
            {
                _logger.LogDebug("Playlist missing required headers");
                return false;
            }

            // プレイリストに#EXT-X-ENDLISTがない場合は不完全（まだ進行中）
            if (!playlistContent.Contains("#EXT-X-ENDLIST"))
            {
                _logger.LogDebug("Playlist missing #EXT-X-ENDLIST (still in progress)");
                return false;
            }

            // セグメントファイルの存在をチェック
            var lines = playlistContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var segmentCount = 0;
            var existingSegmentCount = 0;

            foreach (var line in lines)
            {
                if (line.Trim().EndsWith(".ts"))
                {
                    segmentCount++;
                    var segmentPath = Path.Combine(cacheDir, line.Trim());
                    if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 0)
                    {
                        existingSegmentCount++;
                    }
                }
            }

            // 最低1つのセグメントがあり、全セグメントが存在することを確認
            if (segmentCount == 0)
            {
                _logger.LogDebug("Playlist contains no segments");
                return false;
            }

            if (existingSegmentCount != segmentCount)
            {
                _logger.LogDebug("Missing segments: {Existing}/{Total}", existingSegmentCount, segmentCount);
                return false;
            }

            _logger.LogDebug("Playlist is complete with {Count} segments", segmentCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking playlist completeness: {Message}", ex.Message);
            return false;
        }
    }
}
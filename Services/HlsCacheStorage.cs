using Microsoft.Extensions.Logging;

namespace repos.Services;

public class HlsCacheEntry
{
    public string Content { get; set; } = string.Empty;
    public string CacheDirectory { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessed { get; set; }
}

public interface IHlsCacheStorage
{
    Task<HlsCacheEntry?> GetAsync(string key);
    Task SetAsync(string key, HlsCacheEntry entry);
    Task RemoveAsync(string key);
    Task CleanupExpiredEntriesAsync();
}

public class HlsCacheStorage : IHlsCacheStorage
{
    private readonly ILogger<HlsCacheStorage> _logger;
    private readonly string _hlsSegmentsDirectory;
    private readonly int _maxSize;
    private readonly TimeSpan _maxAge;

    public HlsCacheStorage(ILogger<HlsCacheStorage> logger, int maxSize, TimeSpan maxAge)
    {
        _logger = logger;
        _maxSize = maxSize;
        _maxAge = maxAge;
        _hlsSegmentsDirectory = Path.Combine(AppContext.BaseDirectory, "hls_segments");
        Directory.CreateDirectory(_hlsSegmentsDirectory);
    }

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
                _logger.LogDebug("HLS Cache incomplete for key: {Key}, removing", key);
                await RemoveAsync(key);
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

            var keysToRemove = new List<string>();
            
            foreach (var dir in Directory.GetDirectories(_hlsSegmentsDirectory))
            {
                var playlistPath = Path.Combine(dir, "playlist.m3u8");
                if (File.Exists(playlistPath))
                {
                    var fileInfo = new FileInfo(playlistPath);
                    // 作成時刻と最終アクセス時刻の両方を考慮して、より新しい方を基準にする
                    var lastActiveTime = fileInfo.CreationTime > fileInfo.LastAccessTime 
                        ? fileInfo.CreationTime 
                        : fileInfo.LastAccessTime;
                        
                    if (DateTime.Now - lastActiveTime > _maxAge)
                    {
                        keysToRemove.Add(Path.GetFileName(dir));
                    }
                }
                else
                {
                    // プレイリストファイルがない場合は削除
                    keysToRemove.Add(Path.GetFileName(dir));
                }
            }

            // サイズ制限チェック - 古いものから削除
            var allDirs = Directory.GetDirectories(_hlsSegmentsDirectory)
                .Select(d => new { Path = d, Key = Path.GetFileName(d), CreationTime = Directory.GetCreationTime(d) })
                .OrderBy(d => d.CreationTime)
                .ToList();

            while (allDirs.Count > _maxSize)
            {
                var oldest = allDirs[0];
                keysToRemove.Add(oldest.Key);
                allDirs.RemoveAt(0);
            }

            foreach (var key in keysToRemove.Distinct())
            {
                await RemoveAsync(key);
            }

            if (keysToRemove.Count > 0)
            {
                _logger.LogInformation("HLS Cache cleanup: removed {Count} expired entries", keysToRemove.Count);
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
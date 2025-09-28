using System.Collections.Concurrent;

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
    int Count { get; }
}

public class HlsCacheStorage : IHlsCacheStorage
{
    private readonly ConcurrentDictionary<string, HlsCacheEntry> _cache;
    private readonly Queue<string> _accessOrder; // FIFOのためのキューー
    private readonly object _lockObject = new object();
    private readonly int _maxSize;
    private readonly TimeSpan _maxAge;

    public HlsCacheStorage(int maxSize, TimeSpan maxAge)
    {
        _cache = new ConcurrentDictionary<string, HlsCacheEntry>();
        _accessOrder = new Queue<string>();
        _maxSize = maxSize;
        _maxAge = maxAge;
    }

    public int Count => _cache.Count;

    public async Task<HlsCacheEntry?> GetAsync(string key)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            // 有効期限をチェック
            if (DateTime.Now - entry.CreatedAt > _maxAge)
            {
                await RemoveAsync(key);
                return null;
            }

            entry.LastAccessed = DateTime.Now;
            Console.WriteLine($"HLS Cache hit for key: {key}");
            return entry;
        }

        Console.WriteLine($"HLS Cache miss for key: {key}");
        return null;
    }

    public async Task SetAsync(string key, HlsCacheEntry entry)
    {
        lock (_lockObject)
        {
            // 既存のエントリを削除（存在する場合）
            if (_cache.ContainsKey(key))
            {
                // キューから古いエントリを削除（効率は良くないが、実装は簡単）
                var tempQueue = new Queue<string>();
                while (_accessOrder.Count > 0)
                {
                    var item = _accessOrder.Dequeue();
                    if (item != key)
                    {
                        tempQueue.Enqueue(item);
                    }
                }
                while (tempQueue.Count > 0)
                {
                    _accessOrder.Enqueue(tempQueue.Dequeue());
                }
            }

            // 新しいエントリを追加
            _cache[key] = entry;
            _accessOrder.Enqueue(key);

            // サイズ制限チェックとFIFO削除
            while (_cache.Count > _maxSize && _accessOrder.Count > 0)
            {
                var oldestKey = _accessOrder.Dequeue();
                if (_cache.TryRemove(oldestKey, out var oldEntry))
                {
                    // 古いキャッシュのディレクトリを削除
                    Task.Run(() => CleanupCacheDirectory(oldEntry.CacheDirectory));
                    Console.WriteLine($"HLS Cache evicted (FIFO): {oldestKey}");
                }
            }
        }

        Console.WriteLine($"HLS Cache stored: {key}");
        await Task.CompletedTask;
    }

    public async Task RemoveAsync(string key)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            // キューからも削除
            lock (_lockObject)
            {
                var tempQueue = new Queue<string>();
                while (_accessOrder.Count > 0)
                {
                    var item = _accessOrder.Dequeue();
                    if (item != key)
                    {
                        tempQueue.Enqueue(item);
                    }
                }
                while (tempQueue.Count > 0)
                {
                    _accessOrder.Enqueue(tempQueue.Dequeue());
                }
            }

            // キャッシュディレクトリを削除
            await Task.Run(() => CleanupCacheDirectory(entry.CacheDirectory));
            Console.WriteLine($"HLS Cache removed: {key}");
        }
    }

    public async Task CleanupExpiredEntriesAsync()
    {
        var keysToRemove = new List<string>();
        
        foreach (var kvp in _cache)
        {
            if (DateTime.Now - kvp.Value.CreatedAt > _maxAge)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            await RemoveAsync(key);
        }

        if (keysToRemove.Count > 0)
        {
            Console.WriteLine($"HLS Cache cleanup: removed {keysToRemove.Count} expired entries");
        }
    }

    private void CleanupCacheDirectory(string cacheDir)
    {
        try
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
                Console.WriteLine($"Cleaned up cache directory: {cacheDir}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning up cache directory {cacheDir}: {ex.Message}");
        }
    }
}
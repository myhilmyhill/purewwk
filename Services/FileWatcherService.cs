using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace repos.Services;

public class FileWatcherService : IDisposable
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly LuceneService _luceneService;
    private readonly IConfiguration _configuration;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly Timer _debounceTimer;
    private readonly object _lock = new();
    private bool _disposed = false;

    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2); // 2秒のデバウンス
    private readonly string[] _musicExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma" };

    public FileWatcherService(ILogger<FileWatcherService> logger, LuceneService luceneService, IConfiguration configuration)
    {
        _logger = logger;
        _luceneService = luceneService;
        _configuration = configuration;
        
        // デバウンス用タイマー
        _debounceTimer = new Timer(ProcessPendingChanges, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        
        Initialize();
    }

    private void Initialize()
    {
        var isEnabled = _configuration.GetValue<bool>("FileWatcher:Enabled", true);
        if (!isEnabled)
        {
            _logger.LogInformation("FileWatcher is disabled in configuration");
            return;
        }

        var musicDirectory = _configuration["MusicDirectory"];
        if (string.IsNullOrEmpty(musicDirectory))
        {
            _logger.LogError("MusicDirectory is not configured");
            return;
        }

        CreateWatcher(musicDirectory);
    }

    private void CreateWatcher(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            _logger.LogWarning("Path does not exist or is invalid: {Path}", path);
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                // すべてのファイルを監視（後でフィルタリング）
                Filter = "*.*"
            };

            // イベントハンドラーの設定
            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Changed += OnFileSystemChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnFileWatcherError;

            // ネットワークパスやLinuxマウント対応
            if (IsNetworkPath(path) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // ネットワークパスの場合、より頻繁にポーリング
                watcher.InternalBufferSize = 8192 * 16; // バッファサイズを増加
            }

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
            
            _logger.LogInformation("Started monitoring directory: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create file watcher for path: {Path}", path);
        }
    }

    private bool IsNetworkPath(string path)
    {
        // UNCパス（\\server\share）の検出
        if (path.StartsWith(@"\\"))
            return true;

        // Windowsでマップされたネットワークドライブの検出
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var pathRoot = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(pathRoot))
                {
                    var driveInfo = new DriveInfo(pathRoot);
                    return driveInfo.DriveType == DriveType.Network;
                }
            }
            catch
            {
                // ドライブ情報が取得できない場合はローカルと仮定
                return false;
            }
        }

        // Linux/macOSでのマウントポイント検出（/mnt, /media など）
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var normalizedPath = path.ToLowerInvariant();
            return normalizedPath.StartsWith("/mnt/") || 
                   normalizedPath.StartsWith("/media/") ||
                   normalizedPath.StartsWith("/run/media/");
        }

        return false;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsMusicFile(e.FullPath) && !Directory.Exists(e.FullPath))
            return;

        _logger.LogDebug("File system change detected: {ChangeType} - {Path}", e.ChangeType, e.FullPath);
        
        lock (_lock)
        {
            _pendingChanges[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsMusicFile(e.FullPath) && !IsMusicFile(e.OldFullPath) && 
            !Directory.Exists(e.FullPath) && !Directory.Exists(e.OldFullPath))
            return;

        _logger.LogDebug("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
        
        lock (_lock)
        {
            // 古いパスを削除対象に追加
            _pendingChanges[e.OldFullPath] = DateTime.UtcNow;
            // 新しいパスを追加対象に追加
            _pendingChanges[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnFileWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error occurred");
        
        // エラー発生時は該当するWatcherを再作成
        if (sender is FileSystemWatcher watcher)
        {
            var path = watcher.Path;
            _logger.LogInformation("Attempting to recreate watcher for path: {Path}", path);
            
            try
            {
                watcher.Dispose();
                _watchers.Remove(watcher);
                
                // 少し待ってから再作成
                Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => CreateWatcher(path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recreate file watcher for path: {Path}", path);
            }
        }
    }

    private bool IsMusicFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return _musicExtensions.Contains(extension);
    }

    private void ProcessPendingChanges(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var changesToProcess = new List<string>();

        lock (_lock)
        {
            // デバウンス期間を過ぎた変更を処理対象に移動
            var expiredChanges = _pendingChanges
                .Where(kvp => now - kvp.Value > _debounceDelay)
                .ToList();

            foreach (var change in expiredChanges)
            {
                changesToProcess.Add(change.Key);
                _pendingChanges.TryRemove(change.Key, out _);
            }
        }

        if (changesToProcess.Count > 0)
        {
            _logger.LogInformation("Processing {Count} pending file changes", changesToProcess.Count);
            
            // バックグラウンドでインデックス更新を実行
            Task.Run(() => ProcessChanges(changesToProcess));
        }
    }

    private async Task ProcessChanges(List<string> changedPaths)
    {
        try
        {
            var musicDirectory = _configuration["MusicDirectory"];
            if (string.IsNullOrEmpty(musicDirectory))
            {
                _logger.LogWarning("MusicDirectory is not configured, skipping index update");
                return;
            }

            // メインの音楽ディレクトリのみ更新
            _logger.LogInformation("Updating index for directory: {Directory}", musicDirectory);
            
            await Task.Run(() =>
            {
                try
                {
                    _luceneService.IndexDirectory(musicDirectory);
                    _logger.LogInformation("Successfully updated index for directory: {Directory}", musicDirectory);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update index for directory: {Directory}", musicDirectory);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file changes");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            _debounceTimer?.Dispose();
            
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            
            _watchers.Clear();
            _pendingChanges.Clear();
            
            _logger.LogInformation("FileWatcherService disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing FileWatcherService");
        }
    }
}
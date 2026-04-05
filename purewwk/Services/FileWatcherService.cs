using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Purewwk.Services;

public class FileWatcherService(ILogger<FileWatcherService> _logger, LuceneService _luceneService, IConfiguration _configuration) : IHostedService, IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private Timer? _debounceTimer;
    private readonly object _lock = new();
    private bool _disposed = false;

    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2); // 2遘偵・繝・ヰ繧ｦ繝ｳ繧ｹ
    private readonly string[] _musicExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".cue", ".mid", ".midi" };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting FileWatcherService");
        // 繝・ヰ繧ｦ繝ｳ繧ｹ逕ｨ繧ｿ繧､繝槭・
        _debounceTimer = new Timer(ProcessPendingChanges, null, _debounceDelay, _debounceDelay);
        
        Initialize();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping FileWatcherService");
        _debounceTimer?.Change(Timeout.Infinite, 0);

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
        }
        return Task.CompletedTask;
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
                // 縺吶∋縺ｦ縺ｮ繝輔ぃ繧､繝ｫ繧堤屮隕厄ｼ亥ｾ後〒繝輔ぅ繝ｫ繧ｿ繝ｪ繝ｳ繧ｰ・・
                Filter = "*.*"
            };

            // 繧､繝吶Φ繝医ワ繝ｳ繝峨Λ繝ｼ縺ｮ險ｭ螳・
            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Changed += OnFileSystemChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnFileWatcherError;

            // 繝阪ャ繝医Ρ繝ｼ繧ｯ繝代せ繧Лinux繝槭え繝ｳ繝亥ｯｾ蠢・
            if (IsNetworkPath(path) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // 繝阪ャ繝医Ρ繝ｼ繧ｯ繝代せ縺ｮ蝣ｴ蜷医√ｈ繧企ｻ郢√↓繝昴・繝ｪ繝ｳ繧ｰ
                watcher.InternalBufferSize = 8192 * 16; // 繝舌ャ繝輔ぃ繧ｵ繧､繧ｺ繧貞｢怜刈
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
        // UNC繝代せ・・\server\share・峨・讀懷・
        if (path.StartsWith(@"\\"))
            return true;

        // Windows縺ｧ繝槭ャ繝励＆繧後◆繝阪ャ繝医Ρ繝ｼ繧ｯ繝峨Λ繧､繝悶・讀懷・
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
                // 繝峨Λ繧､繝匁ュ蝣ｱ縺悟叙蠕励〒縺阪↑縺・ｴ蜷医・繝ｭ繝ｼ繧ｫ繝ｫ縺ｨ莉ｮ螳・
                return false;
            }
        }

        // Linux/macOS縺ｧ縺ｮ繝槭え繝ｳ繝医・繧､繝ｳ繝域､懷・・・mnt, /media 縺ｪ縺ｩ・・
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
            // 蜿､縺・ヱ繧ｹ繧貞炎髯､蟇ｾ雎｡縺ｫ霑ｽ蜉
            _pendingChanges[e.OldFullPath] = DateTime.UtcNow;
            // 譁ｰ縺励＞繝代せ繧定ｿｽ蜉蟇ｾ雎｡縺ｫ霑ｽ蜉
            _pendingChanges[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnFileWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error occurred");
        
        // 繧ｨ繝ｩ繝ｼ逋ｺ逕滓凾縺ｯ隧ｲ蠖薙☆繧妓atcher繧貞・菴懈・
        if (sender is FileSystemWatcher watcher)
        {
            var path = watcher.Path;
            _logger.LogInformation("Attempting to recreate watcher for path: {Path}", path);
            
            try
            {
                watcher.Dispose();
                _watchers.Remove(watcher);
                
                // 蟆代＠蠕・▲縺ｦ縺九ｉ蜀堺ｽ懈・
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
            // 繝・ヰ繧ｦ繝ｳ繧ｹ譛滄俣繧帝℃縺弱◆螟画峩繧貞・逅・ｯｾ雎｡縺ｫ遘ｻ蜍・
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
            
            // 繝舌ャ繧ｯ繧ｰ繝ｩ繧ｦ繝ｳ繝峨〒繧､繝ｳ繝・ャ繧ｯ繧ｹ譖ｴ譁ｰ繧貞ｮ溯｡・
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

            _logger.LogInformation("Processing incremental index updates for {Count} changed paths", changedPaths.Count);
            
            await Task.Run(() =>
            {
                try
                {
                    foreach (var path in changedPaths)
                    {
                        // Check if path is within the music directory
                        if (path.StartsWith(musicDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            // Skip non-music files for efficiency (unless it's a directory)
                            if (File.Exists(path) && !IsMusicFile(path))
                            {
                                _logger.LogTrace("Skipping non-music file: {Path}", path);
                                continue;
                            }

                            if (File.Exists(path) || Directory.Exists(path))
                            {
                                // File or directory exists - add/update
                                _luceneService.AddOrUpdatePath(path, musicDirectory);
                                _logger.LogDebug("Updated index for: {Path}", path);
                            }
                            else
                            {
                                // File or directory deleted - remove from index
                                _luceneService.RemoveFromIndex(path);
                                _logger.LogDebug("Removed from index: {Path}", path);
                            }
                        }
                    }
                    
                    _logger.LogInformation("Successfully processed incremental index updates");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process incremental index updates");
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

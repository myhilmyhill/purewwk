using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Purewwk.Plugin;

namespace Purewwk.Plugins.FileWatcher;

public class FileWatcherService(ILogger<FileWatcherService> _logger, IIndexUpdater _indexUpdater, IConfiguration _configuration) : IHostedService, IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private Timer? _debounceTimer;
    private readonly object _lock = new();
    private bool _disposed = false;

    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);
    private readonly string[] _musicExtensions = { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".cue", ".mid", ".midi" };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting FileWatcherService");
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
                Filter = "*.*"
            };

            watcher.Created += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Changed += OnFileSystemChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnFileWatcherError;

            if (IsNetworkPath(path) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                watcher.InternalBufferSize = 8192 * 16;
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
        if (path.StartsWith(@"\\"))
            return true;

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
                return false;
            }
        }

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
            _pendingChanges[e.OldFullPath] = DateTime.UtcNow;
            _pendingChanges[e.FullPath] = DateTime.UtcNow;
        }
    }

    private void OnFileWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error occurred");
        
        if (sender is FileSystemWatcher watcher)
        {
            var path = watcher.Path;
            _logger.LogInformation("Attempting to recreate watcher for path: {Path}", path);
            
            try
            {
                watcher.Dispose();
                _watchers.Remove(watcher);
                
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
                                _indexUpdater.AddOrUpdatePath(path, musicDirectory);
                                _logger.LogDebug("Updated index for: {Path}", path);
                            }
                            else
                            {
                                // File or directory deleted - remove from index
                                _indexUpdater.RemoveFromIndex(path);
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




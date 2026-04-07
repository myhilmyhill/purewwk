using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Purewwk.Plugin;

/// <summary>
/// Base class for services that manage background HLS generation processes.
/// </summary>
public abstract class HlsServiceBase
{
    protected readonly ILogger _logger;
    protected readonly IConfiguration _configuration;
    protected readonly IHttpContextAccessor _httpContextAccessor;

    private readonly Dictionary<string, (CancellationTokenSource Cts, string VariantKey, DateTime StartTime)> _activeProcesses = new();
    protected readonly object _processLock = new object();

    protected HlsServiceBase(ILogger logger, IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
    }

    protected string GetBaseUrl()
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        if (request == null) throw new Exception("HttpContext not available");
        return $"{request.PathBase}/hls?key=";
    }

    protected void RegisterActiveProcess(string id, CancellationTokenSource cts, string variantKey, int maxProcesses = 4)
    {
        lock (_processLock)
        {
            if (_activeProcesses.Count >= maxProcesses)
            {
                var oldest = _activeProcesses.OrderBy(kvp => kvp.Value.StartTime).First();
                _logger.LogInformation("Cancelling oldest HLS process {Id} to make room", oldest.Key);
                oldest.Value.Cts.Cancel();
                _activeProcesses.Remove(oldest.Key);
            }
            _activeProcesses[id] = (cts, variantKey, DateTime.Now);
        }
    }

    protected void UnregisterActiveProcess(string id, CancellationTokenSource cts)
    {
        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var active) && active.Cts == cts)
                _activeProcesses.Remove(id);
        }
    }

    protected bool TryGetActiveProcess(string id, out (CancellationTokenSource Cts, string VariantKey, DateTime StartTime) active)
    {
        lock (_processLock)
        {
            return _activeProcesses.TryGetValue(id, out active);
        }
    }

    protected void CancelAndRemoveActiveProcess(string id)
    {
        lock (_processLock)
        {
            if (_activeProcesses.TryGetValue(id, out var active))
            {
                active.Cts.Cancel();
                _activeProcesses.Remove(id);
            }
        }
    }

    protected async Task WaitForFirstSegment(string id, string cacheDir, string playlistPath, int minSegments = 1, int timeoutSeconds = 60)
    {
        _logger.LogInformation("Waiting for first segment in {Path} for {Id}", playlistPath, id);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            if (File.Exists(playlistPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(playlistPath);
                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var segments = lines.Where(l => l.EndsWith(".ts") && !l.StartsWith("#")).ToList();
                    var isComplete = lines.Any(l => l.Contains("#EXT-X-ENDLIST"));

                    if (segments.Count >= minSegments || (segments.Count > 0 && isComplete))
                    {
                        var segmentPath = Path.Combine(cacheDir, segments.Last());
                        // Just check for existence of the segment file referenced in playlist
                        if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length > 0)
                        {
                            _logger.LogDebug("Found {Count} segments for {Id}. Proceeding.", segments.Count, id);
                            return;
                        }
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning("Retrying playlist read for {Id}: {Msg}", id, ex.Message);
                }
            }
            else if (File.Exists(playlistPath + ".tmp"))
            {
                _logger.LogTrace("Found .tmp playlist for {Id}, waiting for rename", id);
            }

            bool active;
            lock (_processLock) active = _activeProcesses.ContainsKey(id);
            
            if (!active)
            {
                // Process finished, check one last time before giving up
                if (File.Exists(playlistPath))
                {
                    var content = await File.ReadAllTextAsync(playlistPath);
                    if (content.Contains(".ts")) return;
                }
                _logger.LogError("HLS process for {Id} exited prematurely", id);
                throw new Exception("HLS generation process failed or exited early");
            }

            await Task.Delay(500);
        }

        // Timeout diagnostics
        bool dirExists = Directory.Exists(cacheDir);
        string fileList = dirExists ? string.Join(", ", Directory.GetFiles(cacheDir).Select(Path.GetFileName)) : "directory missing";
        _logger.LogError("Timeout waiting for HLS for {Id}. Playlist: {Exists}, Files: {Files}", id, File.Exists(playlistPath), fileList);
        
        throw new Exception($"Timeout waiting for HLS rendering for {id}");
    }
}

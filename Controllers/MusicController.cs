using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using repos.Models;
using repos.Services;

namespace repos.Controllers;

[ApiController]
[Route("rest")]
public class MusicController : ControllerBase
{
    private readonly ILogger<MusicController> _logger;
    private readonly LuceneService _luceneService;
    private readonly PluginManager _pluginManager;
    private readonly IConfiguration _configuration;

    public MusicController(ILogger<MusicController> logger, LuceneService luceneService, PluginManager pluginManager, IConfiguration configuration)
    {
        _logger = logger;
        _luceneService = luceneService;
        _pluginManager = pluginManager;
        _configuration = configuration;
    }

    [HttpGet("getMusicDirectory.view")]
    public IActionResult GetMusicDirectory(string id)
    {
        var children = _luceneService.GetChildren(id);
        var childList = children.Select(c => new ChildResponse
        {
            Id = c["id"],
            Parent = c["parent"],
            IsDir = c["isDir"] == "true",
            Title = c["title"],
            Path = c["path"]
        }).ToList();
        var dirResp = new DirectoryResponse
        {
            Id = id,
            Parent = id == "/" ? null : (string.Join("/", id.Split('/').SkipLast(1)) == "" ? "/" : string.Join("/", id.Split('/').SkipLast(1))),
            Name = id == "/" ? "music" : id.Split('/').Last(),
            Child = childList
        };
        var resp = new SubsonicResponse
        {
            Directory = dirResp
        };
        return Ok(new Dictionary<string, object> { ["subsonic-response"] = resp });
    }

    [HttpGet("test")]
    public IActionResult Test()
    {
        return Ok("Test endpoint is working");
    }

    [HttpGet("debug.view")]
    public IActionResult DebugIndex()
    {
        var docs = new List<Dictionary<string, string>>();
        using var reader = Lucene.Net.Index.DirectoryReader.Open(Lucene.Net.Store.FSDirectory.Open(Path.Combine(_configuration["WorkingDirectory"] ?? AppContext.BaseDirectory, "music_index")));
        var searcher = new Lucene.Net.Search.IndexSearcher(reader);
        var query = new Lucene.Net.Search.MatchAllDocsQuery();
        var hits = searcher.Search(query, 1000).ScoreDocs;
        
        foreach(var hit in hits)
        {
            var doc = searcher.Doc(hit.Doc);
            var d = new Dictionary<string, string>();
            foreach(var field in doc.Fields) d[field.Name] = field.GetStringValue();
            docs.Add(d);
        }
        return Ok(docs);
    }

    [HttpGet("hls.m3u8")]
    public async Task<IActionResult> GetHlsPlaylist(string id, int bitRate = 128, string? audioTrack = null)
    {
        try
        {
            var fileDoc = _luceneService.GetDocumentById(id);
            if (fileDoc == null) return NotFound();

            var extension = Path.GetExtension(fileDoc["path"]).ToLowerInvariant();
            var plugin = _pluginManager.GetPluginForExtension(extension);

            if (plugin == null)
            {
                return BadRequest($"No plugin found for extension {extension}");
            }

            var metadata = new PureWwk.Plugin.Abstractions.MediaFileMetadata
            {
                Id = id,
                Path = fileDoc["path"],
                Attributes = fileDoc
            };

            return await plugin.HandlePlaybackAsync(metadata, HttpContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in playback endpoint: {Message}", ex.Message);
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }

    [HttpGet("playback-info")]
    public IActionResult GetPlaybackInfo(string id)
    {
        var fileDoc = _luceneService.GetDocumentById(id);
        if (fileDoc == null) return NotFound();

        var extension = Path.GetExtension(fileDoc["path"]).ToLowerInvariant();
        var plugin = _pluginManager.GetPluginForExtension(extension);

        if (plugin == null) return NotFound("No plugin for this extension");

        return Ok(new
        {
            PlayerType = plugin.GetPlayerType(extension),
            // Plugin could also return more info here
        });
    }

    [HttpGet("download.view")]
    public IActionResult Download(string id)
    {
        try
        {
            _logger.LogDebug("Download request received - ID: {Id}", id);

            var fileDoc = _luceneService.GetDocumentById(id);

            if (fileDoc == null || fileDoc["isDir"] == "true")
            {
                _logger.LogWarning("File not found with id: {Id}", id);
                return NotFound($"Media file not found for id: {id}");
            }

            var fullPath = fileDoc["path"];

            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogWarning("File not found on disk: {FullPath}", fullPath);
                return NotFound($"Media file not found on disk: {fullPath}");
            }

            _logger.LogDebug("Serving original file: {FullPath}", fullPath);

            // Get MIME type based on plugin
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            var plugin = _pluginManager.GetPluginForExtension(extension);
            
            var contentType = plugin?.GetMimeType(extension) ?? "application/octet-stream";

            // Set the filename for download
            var fileName = Path.GetFileName(fullPath);
            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");

            // Return the original file without any transcoding (passthrough)
            return PhysicalFile(fullPath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in download endpoint: {Message}", ex.Message);
            return StatusCode(500, $"Error downloading file: {ex.Message}");
        }
    }

    // New hierarchical path-based segment route: /hls?id=/path/to/segment
    [HttpGet("~/hls")]
    public IActionResult GetHlsSegmentHierarchical(string key)
    {
        try
        {
            _logger.LogDebug("Looking for HLS segment (hierarchical) - Key: {Key}", key);
            var workingDir = _configuration["WorkingDirectory"];
            var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
            var hlsSegmentsDir = Path.Combine(baseDir, "hls_segments");

            if (string.IsNullOrWhiteSpace(key)) return NotFound();

            // 正規化 & ディレクトリ逸脱防止
            var fullBase = Path.GetFullPath(hlsSegmentsDir);
            var fullPath = Path.GetFullPath($"{hlsSegmentsDir}{key}");
            if (!fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Rejected path traversal attempt: {Key}", key);
                return Forbid();
            }

            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogDebug("Hierarchical segment not found: {FullPath}", fullPath);
                return NotFound();
            }

            var fileName = Path.GetFileName(fullPath);
            var contentType = fileName.EndsWith(".ts") ? "video/MP2T" : "application/vnd.apple.mpegurl";
            return PhysicalFile(fullPath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving hierarchical HLS segment: {Message}", ex.Message);
            return StatusCode(500, $"Error serving segment: {ex.Message}");
        }
    }
}

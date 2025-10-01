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
    private readonly HlsService _hlsService;
    private readonly IConfiguration _configuration;

    public MusicController(ILogger<MusicController> logger, LuceneService luceneService, HlsService hlsService, IConfiguration configuration)
    {
        _logger = logger;
        _luceneService = luceneService;
        _hlsService = hlsService;
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

    [HttpGet("hls.m3u8")]
    public async Task<IActionResult> GetHlsPlaylist(string id, int bitRate = 128, string? audioTrack = null)
    {
        try
        {
            // デバッグ情報をログに出力
            _logger.LogDebug("HLS request received - ID: {Id}, BitRate: {BitRate}", id, bitRate);

            var playlist = await _hlsService.GenerateHlsPlaylist(id, new[] { bitRate }, audioTrack);
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, proxy-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
            return Content(playlist, "application/vnd.apple.mpegurl");
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "File not found: {Message}", ex.Message);
            return NotFound($"Media file not found for id: {id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating HLS playlist: {Message}", ex.Message);
            return StatusCode(500, $"Error generating HLS playlist: {ex.Message}");
        }
    }

    [HttpGet("download.view")]
    public IActionResult Download(string id)
    {
        try
        {
            _logger.LogDebug("Download request received - ID: {Id}", id);

            // Get file path from Lucene index (similar to HlsService logic)
            var directoryName = "/";
            if (id.Contains("/") && id.LastIndexOf("/") > 0)
            {
                directoryName = id.Substring(0, id.LastIndexOf("/"));
            }

            var children = _luceneService.GetChildren(directoryName);
            var fileDoc = children.FirstOrDefault(c => c["id"] == id && c["isDir"] == "false");

            if (fileDoc == null)
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

            // Get MIME type based on file extension
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".mp3" => "audio/mpeg",
                ".flac" => "audio/flac",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".opus" => "audio/opus",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".wv" => "audio/x-wavpack",
                ".ape" => "audio/x-ape",
                ".wma" => "audio/x-ms-wma",
                _ => "application/octet-stream"
            };

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

    [Obsolete("Use hierarchical path route /rest/hls/path/{*rel}")]
    [HttpGet("hls/{cacheKey}/{*path}")]
    public IActionResult GetHlsSegmentLegacy(string cacheKey, string path)
    {
        try
        {
            _logger.LogDebug("(LEGACY) Looking for HLS segment - CacheKey: {CacheKey}, Path: {Path}", cacheKey, path);

            // Use the same directory structure as HlsService
            var workingDir = _configuration["WorkingDirectory"];
            var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;

            // The HlsService generates cache keys with bitrate info
            // We need to find the correct cache directory by scanning for directories that start with the id
            var hlsSegmentsDir = Path.Combine(baseDir, "hls_segments");

            if (!Directory.Exists(hlsSegmentsDir))
            {
                _logger.LogWarning("HLS segments directory not found: {HlsSegmentsDir}", hlsSegmentsDir);
                return NotFound($"HLS segments directory not found");
            }

            // Directly map cacheKey -> directory
            var targetDir = Path.Combine(hlsSegmentsDir, cacheKey); // legacy layout
            if (!Directory.Exists(targetDir))
            {
                _logger.LogWarning("Cache directory not found for cacheKey: {CacheKey}", cacheKey);
                return NotFound($"Cache directory not found");
            }

            var fileName = Path.GetFileName(path);
            var segmentPathFinal = Path.Combine(targetDir, fileName);
            _logger.LogDebug("Checking segment path: {SegmentPathFinal}", segmentPathFinal);
            if (System.IO.File.Exists(segmentPathFinal))
            {
                _logger.LogDebug("Found segment file: {SegmentPathFinal}", segmentPathFinal);
                var contentType = fileName.EndsWith(".ts") ? "video/MP2T" : "application/vnd.apple.mpegurl";
                return PhysicalFile(segmentPathFinal, contentType);
            }

            _logger.LogWarning("Segment not found: {FileName} in cacheKey: {CacheKey}", fileName, cacheKey);
            return NotFound($"Segment not found: {fileName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving HLS segment: {Message}", ex.Message);
            return StatusCode(500, $"Error serving segment: {ex.Message}");
        }
    }

    // New hierarchical path-based segment route: /rest/hls/path/<relativeId>/<variantKey>/segment_xxx.ts
    [HttpGet("hls/path/{*rel}")]
    public IActionResult GetHlsSegmentHierarchical(string rel)
    {
        try
        {
            _logger.LogDebug("Looking for HLS segment (hierarchical) - Rel: {Rel}", rel);
            var workingDir = _configuration["WorkingDirectory"];
            var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
            var hlsSegmentsDir = Path.Combine(baseDir, "hls_segments");

            if (string.IsNullOrWhiteSpace(rel)) return NotFound();

            // 正規化 & ディレクトリ逸脱防止
            var fullBase = Path.GetFullPath(hlsSegmentsDir);
            var fullPath = Path.GetFullPath(Path.Combine(hlsSegmentsDir, rel.Replace('\\', '/')));
            if (!fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Rejected path traversal attempt: {Rel}", rel);
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

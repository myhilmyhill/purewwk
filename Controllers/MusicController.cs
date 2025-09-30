using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;
using repos.Models;
using repos.Services;

namespace repos.Controllers;

[ApiController]
[Route("rest")]
public class MusicController : ControllerBase
{
    private readonly LuceneService _luceneService;
    private readonly HlsService _hlsService;
    private readonly IConfiguration _configuration;

    public MusicController(LuceneService luceneService, HlsService hlsService, IConfiguration configuration)
    {
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
            Parent = id == "/" ? "" : string.Join("/", id.Split('/').SkipLast(1)),
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
            Console.WriteLine($"HLS request received - ID: {id}, BitRate: {bitRate}");

            var playlist = await _hlsService.GenerateHlsPlaylist(id, new[] { bitRate }, audioTrack);
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, proxy-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
            return Content(playlist, "application/vnd.apple.mpegurl");
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine($"File not found: {ex.Message}");
            return NotFound($"Media file not found for id: {id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating HLS playlist: {ex.Message}");
            return StatusCode(500, $"Error generating HLS playlist: {ex.Message}");
        }
    }

    [HttpGet("download.view")]
    public IActionResult Download(string id)
    {
        try
        {
            Console.WriteLine($"Download request received - ID: {id}");

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
                Console.WriteLine($"File not found with id: {id}");
                return NotFound($"Media file not found for id: {id}");
            }

            var fullPath = fileDoc["path"];

            if (!System.IO.File.Exists(fullPath))
            {
                Console.WriteLine($"File not found on disk: {fullPath}");
                return NotFound($"Media file not found on disk: {fullPath}");
            }

            Console.WriteLine($"Serving original file: {fullPath}");

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
            Console.WriteLine($"Error in download endpoint: {ex.Message}");
            return StatusCode(500, $"Error downloading file: {ex.Message}");
        }
    }

    [Obsolete("Use hierarchical path route /rest/hls/path/{*rel}")]
    [HttpGet("hls/{cacheKey}/{*path}")]
    public IActionResult GetHlsSegmentLegacy(string cacheKey, string path)
    {
        try
        {
            Console.WriteLine($"(LEGACY) Looking for HLS segment - CacheKey: {cacheKey}, Path: {path}");

            // Use the same directory structure as HlsService
            var workingDir = _configuration["WorkingDirectory"];
            var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;

            // The HlsService generates cache keys with bitrate info
            // We need to find the correct cache directory by scanning for directories that start with the id
            var hlsSegmentsDir = Path.Combine(baseDir, "hls_segments");

            if (!Directory.Exists(hlsSegmentsDir))
            {
                Console.WriteLine($"HLS segments directory not found: {hlsSegmentsDir}");
                return NotFound($"HLS segments directory not found");
            }

            // Directly map cacheKey -> directory
            var targetDir = Path.Combine(hlsSegmentsDir, cacheKey); // legacy layout
            if (!Directory.Exists(targetDir))
            {
                Console.WriteLine($"Cache directory not found for cacheKey: {cacheKey}");
                return NotFound($"Cache directory not found");
            }

            var fileName = Path.GetFileName(path);
            var segmentPathFinal = Path.Combine(targetDir, fileName);
            Console.WriteLine($"Checking segment path: {segmentPathFinal}");
            if (System.IO.File.Exists(segmentPathFinal))
            {
                Console.WriteLine($"Found segment file: {segmentPathFinal}");
                var contentType = fileName.EndsWith(".ts") ? "video/MP2T" : "application/vnd.apple.mpegurl";
                return PhysicalFile(segmentPathFinal, contentType);
            }

            Console.WriteLine($"Segment not found: {fileName} in cacheKey: {cacheKey}");
            return NotFound($"Segment not found: {fileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error serving HLS segment: {ex.Message}");
            return StatusCode(500, $"Error serving segment: {ex.Message}");
        }
    }

    // New hierarchical path-based segment route: /rest/hls/path/<relativeId>/<variantKey>/segment_xxx.ts
    [HttpGet("hls/path/{*rel}")]
    public IActionResult GetHlsSegmentHierarchical(string rel)
    {
        try
        {
            Console.WriteLine($"Looking for HLS segment (hierarchical) - Rel: {rel}");
            var workingDir = _configuration["WorkingDirectory"];
            var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
            var hlsSegmentsDir = Path.Combine(baseDir, "hls_segments");

            if (string.IsNullOrWhiteSpace(rel)) return NotFound();

            // 正規化 & ディレクトリ逸脱防止
            var fullBase = Path.GetFullPath(hlsSegmentsDir);
            var fullPath = Path.GetFullPath(Path.Combine(hlsSegmentsDir, rel.Replace('\\', '/')));
            if (!fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Rejected path traversal attempt: " + rel);
                return Forbid();
            }

            if (!System.IO.File.Exists(fullPath))
            {
                Console.WriteLine($"Hierarchical segment not found: {fullPath}");
                return NotFound();
            }

            var fileName = Path.GetFileName(fullPath);
            var contentType = fileName.EndsWith(".ts") ? "video/MP2T" : "application/vnd.apple.mpegurl";
            return PhysicalFile(fullPath, contentType);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error serving hierarchical HLS segment: {ex.Message}");
            return StatusCode(500, $"Error serving segment: {ex.Message}");
        }
    }
}

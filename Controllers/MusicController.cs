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

    [HttpGet("hls/{id}/{*path}")]
    public IActionResult GetHlsSegment(string id, string path)
    {
        try
        {
            Console.WriteLine($"Looking for HLS segment - ID: {id}, Path: {path}");
            
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
            
            // Look for cache directories that match this ID
            var possibleDirs = Directory.GetDirectories(hlsSegmentsDir)
                .Where(dir =>
                {
                    var dirName = Path.GetFileName(dir);
                    var cleanId = id.TrimStart('/').Replace("/", "_").Replace("\\", "_");
                    return dirName.StartsWith(cleanId + "_") || dirName == cleanId;
                })
                .ToList();
            
            Console.WriteLine($"Found {possibleDirs.Count} possible cache directories for id: {id}");
            foreach (var dir in possibleDirs)
            {
                Console.WriteLine($"  - {Path.GetFileName(dir)}");
            }
            
            // Try to find the segment file in any of the matching directories
            var fileName = Path.GetFileName(path);
            foreach (var cacheDir in possibleDirs)
            {
                var segmentPath = Path.Combine(cacheDir, fileName);
                Console.WriteLine($"Checking segment path: {segmentPath}");
                
                if (System.IO.File.Exists(segmentPath))
                {
                    Console.WriteLine($"Found segment file: {segmentPath}");
                    var contentType = fileName.EndsWith(".ts") ? "video/MP2T" : "application/vnd.apple.mpegurl";
                    return PhysicalFile(segmentPath, contentType);
                }
            }
            
            Console.WriteLine($"Segment not found: {fileName} for id: {id}");
            return NotFound($"Segment not found: {fileName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error serving HLS segment: {ex.Message}");
            return StatusCode(500, $"Error serving segment: {ex.Message}");
        }
    }
}

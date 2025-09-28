using Microsoft.AspNetCore.Mvc;
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

    public MusicController(LuceneService luceneService, HlsService hlsService)
    {
        _luceneService = luceneService;
        _hlsService = hlsService;
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

    [HttpGet("hls/{id}/{*path}")]
    public IActionResult GetHlsSegment(string id, string path)
    {
        // Original ID might have been prefixed with /, so we need to check both variants
        var segmentDir1 = Path.Combine(Path.GetTempPath(), "hls_segments", id.Replace("/", "_"));
        var segmentDir2 = Path.Combine(Path.GetTempPath(), "hls_segments", ("/" + id).Replace("/", "_"));
        
        var segmentPath1 = Path.Combine(segmentDir1, path);
        var segmentPath2 = Path.Combine(segmentDir2, path);
        
        string segmentPath;
        if (System.IO.File.Exists(segmentPath1))
        {
            segmentPath = segmentPath1;
        }
        else if (System.IO.File.Exists(segmentPath2))
        {
            segmentPath = segmentPath2;
        }
        else
        {
            return NotFound($"Segment not found: {path}");
        }
        
        var contentType = path.EndsWith(".ts") ? "video/MP2T" : "application/vnd.apple.mpegurl";
        return PhysicalFile(segmentPath, contentType);
    }
}

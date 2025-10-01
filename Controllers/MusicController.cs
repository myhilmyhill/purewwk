using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using repos.Models;
using repos.Services;

namespace repos.Controllers;

// Helper class to clean up temporary files after response is sent
public class FileCleanupDisposable : IDisposable
{
    private readonly string _filePath;
    
    public FileCleanupDisposable(string filePath)
    {
        _filePath = filePath;
    }
    
    public void Dispose()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                Console.WriteLine($"Cleaned up temporary file: {_filePath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning up temporary file {_filePath}: {ex.Message}");
        }
    }
}

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

            // Check if this is a CUE track
            bool isCueTrack = fileDoc.ContainsKey("isCueTrack") && fileDoc["isCueTrack"] == "true";
            
            if (isCueTrack)
            {
                // Extract CUE track information
                string? startTime = fileDoc.ContainsKey("startTime") ? fileDoc["startTime"] : null;
                string? endTime = fileDoc.ContainsKey("endTime") ? fileDoc["endTime"] : null;
                string trackTitle = fileDoc.ContainsKey("title") ? fileDoc["title"] : "track";
                
                if (string.IsNullOrEmpty(startTime))
                {
                    return BadRequest("CUE track missing start time information");
                }

                Console.WriteLine($"Extracting CUE track: start={startTime}s, end={endTime ?? "EOF"}");

                // Use FFmpeg to extract the track segment
                var workingDir = _configuration["WorkingDirectory"];
                var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
                var tempDir = Path.Combine(baseDir, "temp_downloads");
                Directory.CreateDirectory(tempDir);

                // Generate a unique filename for the extracted track
                var outputFileName = $"{Guid.NewGuid()}.mp3";
                var outputPath = Path.Combine(tempDir, outputFileName);

                try
                {
                    // Build FFmpeg command to extract the track
                    var ssArg = $"-ss {startTime}";
                    var toArg = !string.IsNullOrEmpty(endTime) ? $"-to {endTime}" : "";
                    var ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH")
                        ?? _configuration["FFmpeg:Path"]
                        ?? "ffmpeg";

                    var ffmpegArgs = $"-y -v error {ssArg} {toArg} -i \"{fullPath}\" -c:a libmp3lame -q:a 2 \"{outputPath}\"";
                    Console.WriteLine($"FFmpeg command: {ffmpegPath} {ffmpegArgs}");

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = ffmpegPath,
                            Arguments = ffmpegArgs,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    process.WaitForExit(30000); // 30 second timeout

                    if (process.ExitCode != 0)
                    {
                        var error = process.StandardError.ReadToEnd();
                        Console.WriteLine($"FFmpeg error: {error}");
                        return StatusCode(500, $"Error extracting CUE track: {error}");
                    }

                    if (!System.IO.File.Exists(outputPath))
                    {
                        return StatusCode(500, "Failed to extract CUE track");
                    }

                    Console.WriteLine($"Successfully extracted CUE track to: {outputPath}");

                    // Return the extracted file and schedule cleanup
                    var downloadFileName = $"{trackTitle}.mp3";
                    Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{downloadFileName}\"");

                    // Schedule cleanup of temp file after response is sent
                    Response.RegisterForDispose(new FileCleanupDisposable(outputPath));

                    return PhysicalFile(outputPath, "audio/mpeg");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error extracting CUE track: {ex.Message}");
                    // Clean up temp file if it exists
                    if (System.IO.File.Exists(outputPath))
                    {
                        try { System.IO.File.Delete(outputPath); } catch { }
                    }
                    return StatusCode(500, $"Error extracting CUE track: {ex.Message}");
                }
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

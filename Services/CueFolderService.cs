using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace repos.Services;

public class CueFolderService(
    ILogger<CueFolderService> _logger,
    CueService _cueService)
{
    /// <summary>
    /// Returns virtual tracks for a CUE file as directory entries.
    /// </summary>
    public List<CueTrackInfo> GetVirtualTracks(string cueFilePath)
    {
        var tracks = new List<CueTrackInfo>();
        try
        {
            var sheet = _cueService.ParseCue(cueFilePath);
            var cueDir = Path.GetDirectoryName(cueFilePath) ?? "";

            foreach (var file in sheet.Files)
            {
                var sourceAudioPath = _cueService.ResolveAudioFile(cueFilePath, file.FileName);
                if (sourceAudioPath == null)
                {
                    _logger.LogWarning("Could not resolve audio file for CUE: {CuePath}, File: {FileName}", cueFilePath, file.FileName);
                    continue;
                }

                foreach (var track in file.Tracks)
                {
                    var trackNum = track.Number.ToString("00");
                    var trackTitle = !string.IsNullOrWhiteSpace(track.Title) ? track.Title : $"Track {trackNum}";
                    var trackArtist = !string.IsNullOrWhiteSpace(track.Performer) ? track.Performer : (!string.IsNullOrWhiteSpace(sheet.Performer) ? sheet.Performer : "Unknown Artist");

                    // Virtual extension should match source for client compatibility
                    var extension = Path.GetExtension(sourceAudioPath);
                    var virtualFileName = $"{trackNum} - {trackTitle}{extension}";

                    tracks.Add(new CueTrackInfo
                    {
                        TrackNumber = track.Number,
                        Title = trackTitle,
                        Artist = trackArtist,
                        VirtualFileName = virtualFileName,
                        SourceAudioPath = sourceAudioPath,
                        StartTime = track.StartTime,
                        Duration = track.Duration
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting virtual tracks for CUE: {Path}", cueFilePath);
        }

        return tracks;
    }
}

public class CueTrackInfo
{
    public int TrackNumber { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string VirtualFileName { get; set; } = "";
    public string SourceAudioPath { get; set; } = "";
    public TimeSpan StartTime { get; set; }
    public TimeSpan? Duration { get; set; }
}

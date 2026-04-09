using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Purewwk.Plugin;

namespace Purewwk.Plugins.Cue;

public class CueIndexingPlugin(ICueFolderService _cueFolderService, IEnumerable<IPlayablePlugin> _playbackPlugins) : IIndexingPlugin
{
    public string Name => "CUE Indexing Provider";
    public string[] SupportedExtensions => new[] { ".cue" };

    public bool CanHandle(string path)
    {
        return path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandleExtension(string extension) => SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<MediaItem> GetEntries(string path)
    {
        var result = new List<MediaItem>();
        try
        {
            var tracks = _cueFolderService.GetVirtualTracks(path).OrderBy(t => t.TrackNumber);
            foreach (var track in tracks)
            {
                var ext = Path.GetExtension(track.SourceAudioPath).ToLowerInvariant();
                var player = _playbackPlugins.FirstOrDefault(p => p.CanHandle(ext));

                var entry = new MediaFile
                {
                    Id = track.TrackNumber.ToString("00"),
                    Parent = string.Empty,
                    Title = $"{track.TrackNumber:00} - {track.Title}",
                    Path = track.SourceAudioPath,
                    StartTime = track.StartTime.TotalSeconds,
                    Duration = track.Duration?.TotalSeconds,
                    Player = player ?? throw new Exception($"Player for {ext} not found")
                };
                
                result.Add(entry);
            }
        }
        catch { }
        return result;
    }

    bool IPlugin.CanHandle(string extension) => CanHandleExtension(extension);
}


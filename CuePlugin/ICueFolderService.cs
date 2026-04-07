using System;
using System.Collections.Generic;

namespace Purewwk.Plugins.Cue;

public interface ICueFolderService
{
    List<CueTrackInfo> GetVirtualTracks(string cueFilePath);
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



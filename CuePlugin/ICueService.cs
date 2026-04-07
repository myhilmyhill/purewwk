using System;
using System.Collections.Generic;

namespace Purewwk.Plugins.Cue;

public interface ICueService
{
    CueSheet ParseCue(string filePath);
    CueSheet ParseCue(byte[] data, string filePath);
    string? ResolveAudioFile(string cuePath, string cueFileName);
}

public class CueSheet
{
    public string? Path { get; set; }
    public string? Title { get; set; }
    public string? Performer { get; set; }
    public List<CueFile> Files { get; set; } = new();
}

public class CueFile
{
    public string FileName { get; set; } = "";
    public List<CueTrack> Tracks { get; set; } = new();
}

public class CueTrack
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Performer { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan? Duration { get; set; }
}



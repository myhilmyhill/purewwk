using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace repos.Services;

public class CueService
{
    private readonly ILogger<CueService> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly string[] _audioExtensions = [".mp3", ".flac", ".wav", ".ape", ".wv", ".m4a", ".tta", ".tak"];

    public CueService(ILogger<CueService> logger, IFileSystem fileSystem)
    {
        _logger = logger;
        _fileSystem = fileSystem;
    }

    public CueSheet ParseCue(string filePath)
    {
        byte[] data;
        try
        {
            data = _fileSystem.ReadAllBytes(filePath);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to read CUE file: {FilePath}", filePath);
            throw;
        }
        return ParseCue(data, filePath);
    }

    public CueSheet ParseCue(byte[] data, string filePath)
    {
        string content;
        // Parse as UTF-8 (strict, will throw if invalid chars found which is expected if we don't support other encodings)
        content = new UTF8Encoding(false, true).GetString(data);

        return ParseCueContent(content, filePath);
    }

    private CueSheet ParseCueContent(string content, string filePath)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var sheet = new CueSheet { Path = filePath };
        CueFile? currentFile = null;
        CueTrack? currentTrack = null;

        foreach (var line in lines)
        {
            var trim = line.Trim();
            if (string.IsNullOrWhiteSpace(trim) || trim.StartsWith("REM")) continue;

            var fileMatch = Regex.Match(trim, "^FILE\\s+\"(.+)\"\\s+(\\w+)", RegexOptions.IgnoreCase);
            if (fileMatch.Success)
            {
                currentFile = new CueFile { FileName = fileMatch.Groups[1].Value };
                sheet.Files.Add(currentFile);
                currentTrack = null; 
                continue;
            }

            var trackMatch = Regex.Match(trim, "^TRACK\\s+(\\d+)\\s+(\\w+)", RegexOptions.IgnoreCase);
            if (trackMatch.Success)
            {
                if (currentFile == null)
                {
                    // Some CUE files have TRACK before FILE (not standard but happens)
                    if (sheet.Files.Count == 0)
                    {
                         // Try to guess audio file name from CUE name
                         var baseName = Path.GetFileNameWithoutExtension(filePath);
                         currentFile = new CueFile { FileName = baseName }; 
                         sheet.Files.Add(currentFile);
                    }
                    else
                    {
                        currentFile = sheet.Files[^1];
                    }
                }
                currentTrack = new CueTrack { Number = int.Parse(trackMatch.Groups[1].Value) };
                currentFile.Tracks.Add(currentTrack);
                continue;
            }

            var indexMatch = Regex.Match(trim, "^INDEX\\s+(\\d+)\\s+(\\d{2,3}):(\\d{2}):(\\d{2})", RegexOptions.IgnoreCase);
            if (indexMatch.Success)
            {
                int indexType = int.Parse(indexMatch.Groups[1].Value);
                if (indexType == 1 && currentTrack != null)
                {
                    int mm = int.Parse(indexMatch.Groups[2].Value);
                    int ss = int.Parse(indexMatch.Groups[3].Value);
                    int ff = int.Parse(indexMatch.Groups[4].Value);
                    double totalSeconds = (mm * 60) + ss + (ff / 75.0);
                    currentTrack.StartTime = TimeSpan.FromSeconds(totalSeconds);
                }
                continue;
            }

            var titleMatch = Regex.Match(trim, "^TITLE\\s+\"(.+)\"", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                var val = titleMatch.Groups[1].Value;
                if (currentTrack != null) currentTrack.Title = val;
                else sheet.Title = val;
                continue;
            }

            var perfMatch = Regex.Match(trim, "^PERFORMER\\s+\"(.+)\"", RegexOptions.IgnoreCase);
            if (perfMatch.Success)
            {
                var val = perfMatch.Groups[1].Value;
                if (currentTrack != null) currentTrack.Performer = val;
                else sheet.Performer = val;
                continue;
            }
        }

        // Calculate durations
        foreach (var file in sheet.Files)
        {
            for (int i = 0; i < file.Tracks.Count; i++)
            {
                var track = file.Tracks[i];
                if (i + 1 < file.Tracks.Count)
                {
                    track.Duration = file.Tracks[i + 1].StartTime - track.StartTime;
                }
            }
        }

        return sheet;
    }

    public string? ResolveAudioFile(string cuePath, string cueFileName)
    {
        var cueDir = Path.GetDirectoryName(cuePath) ?? "";
        var fullPath = Path.Combine(cueDir, cueFileName);
        
        if (_fileSystem.FileExists(fullPath)) return fullPath;

        // Try same name with common audio extensions
        var baseName = Path.GetFileNameWithoutExtension(cueFileName);
        foreach (var ext in _audioExtensions)
        {
            var p = Path.Combine(cueDir, baseName + ext);
            if (_fileSystem.FileExists(p)) return p;
        }

        // Try CUE file's own base name
        var cueBaseName = Path.GetFileNameWithoutExtension(cuePath);
        foreach (var ext in _audioExtensions)
        {
            var p = Path.Combine(cueDir, cueBaseName + ext);
            if (_fileSystem.FileExists(p)) return p;
        }

        return null;
    }
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

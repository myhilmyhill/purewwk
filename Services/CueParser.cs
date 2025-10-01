using System.Text.RegularExpressions;

namespace repos.Services;

public class CueTrack
{
    public int TrackNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Performer { get; set; } = string.Empty;
    public TimeSpan StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
}

public class CueSheet
{
    public string Title { get; set; } = string.Empty;
    public string Performer { get; set; } = string.Empty;
    public string AudioFileName { get; set; } = string.Empty;
    public List<CueTrack> Tracks { get; set; } = new List<CueTrack>();
}

public class CueParser
{
    public static CueSheet? ParseCueFile(string cueFilePath)
    {
        if (!File.Exists(cueFilePath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(cueFilePath);
            var cueSheet = new CueSheet();
            CueTrack? currentTrack = null;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Parse TITLE at album level
                if (trimmedLine.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase) && currentTrack == null)
                {
                    cueSheet.Title = ExtractQuotedValue(trimmedLine);
                }
                // Parse PERFORMER at album level
                else if (trimmedLine.StartsWith("PERFORMER", StringComparison.OrdinalIgnoreCase) && currentTrack == null)
                {
                    cueSheet.Performer = ExtractQuotedValue(trimmedLine);
                }
                // Parse FILE
                else if (trimmedLine.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(trimmedLine, @"FILE\s+""([^""]+)""", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        cueSheet.AudioFileName = match.Groups[1].Value;
                    }
                }
                // Parse TRACK
                else if (trimmedLine.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    // Save previous track if exists
                    if (currentTrack != null)
                    {
                        cueSheet.Tracks.Add(currentTrack);
                    }

                    var match = Regex.Match(trimmedLine, @"TRACK\s+(\d+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        currentTrack = new CueTrack
                        {
                            TrackNumber = int.Parse(match.Groups[1].Value),
                            Performer = cueSheet.Performer // Inherit album performer by default
                        };
                    }
                }
                // Parse TITLE at track level
                else if (trimmedLine.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase) && currentTrack != null)
                {
                    currentTrack.Title = ExtractQuotedValue(trimmedLine);
                }
                // Parse PERFORMER at track level
                else if (trimmedLine.StartsWith("PERFORMER", StringComparison.OrdinalIgnoreCase) && currentTrack != null)
                {
                    currentTrack.Performer = ExtractQuotedValue(trimmedLine);
                }
                // Parse INDEX 01 (start time)
                else if (trimmedLine.StartsWith("INDEX", StringComparison.OrdinalIgnoreCase) && currentTrack != null)
                {
                    var match = Regex.Match(trimmedLine, @"INDEX\s+01\s+(\d+):(\d+):(\d+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var minutes = int.Parse(match.Groups[1].Value);
                        var seconds = int.Parse(match.Groups[2].Value);
                        var frames = int.Parse(match.Groups[3].Value);
                        
                        // CUE frames are 1/75 of a second
                        var totalSeconds = minutes * 60 + seconds + frames / 75.0;
                        currentTrack.StartTime = TimeSpan.FromSeconds(totalSeconds);
                    }
                }
            }

            // Add the last track
            if (currentTrack != null)
            {
                cueSheet.Tracks.Add(currentTrack);
            }

            // Calculate end times for each track (next track's start time)
            for (int i = 0; i < cueSheet.Tracks.Count - 1; i++)
            {
                cueSheet.Tracks[i].EndTime = cueSheet.Tracks[i + 1].StartTime;
            }
            // Last track has no end time (plays to the end of file)

            return cueSheet;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing CUE file {cueFilePath}: {ex.Message}");
            return null;
        }
    }

    private static string ExtractQuotedValue(string line)
    {
        var match = Regex.Match(line, @"""([^""]*)""");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    public static string? FindAudioFileForCue(string cueFilePath)
    {
        var cueSheet = ParseCueFile(cueFilePath);
        if (cueSheet == null || string.IsNullOrEmpty(cueSheet.AudioFileName))
        {
            return null;
        }

        var cueDirectory = Path.GetDirectoryName(cueFilePath);
        if (string.IsNullOrEmpty(cueDirectory))
        {
            return null;
        }

        var audioFilePath = Path.Combine(cueDirectory, cueSheet.AudioFileName);
        return File.Exists(audioFilePath) ? audioFilePath : null;
    }
}

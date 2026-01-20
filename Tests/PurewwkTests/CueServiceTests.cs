using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using repos.Services;
using Xunit;
using Xunit.Abstractions;

namespace PurewwkTests;

public class CueServiceTests
{
    private readonly ITestOutputHelper _output;
    private readonly CueService _cueService;
    private readonly string _musicDir;

    public CueServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _cueService = new CueService(NullLogger<CueService>.Instance);
        
        // Using the absolute path as in the environment
        _musicDir = @"c:\Users\ebyck\source\repos\purewwk-1\Music";
    }

    [Fact]
    public void ParseCue_WithValidUtf8_ParsesCorrectly()
    {
        var content = """
            REM GENRE Fusion
            PERFORMER "Test Artist"
            TITLE "Test Album"
            FILE "TestAudio.wav" WAVE
              TRACK 01 AUDIO
                TITLE "Song 1"
                PERFORMER "Test Artist"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Song 2"
                PERFORMER "Test Artist"
                INDEX 01 03:00:00
            """;
        
        // Use UTF-8 (No BOM)
        var bytes = new UTF8Encoding(false).GetBytes(content);
        var sheet = _cueService.ParseCue(bytes, "dummy_utf8.cue");

        Assert.NotNull(sheet);
        Assert.Equal("Test Album", sheet.Title);
        Assert.Equal("Test Artist", sheet.Performer);
        Assert.Single(sheet.Files);
        Assert.Equal("TestAudio.wav", sheet.Files[0].FileName);
        Assert.Equal(2, sheet.Files[0].Tracks.Count);
        
        var t1 = sheet.Files[0].Tracks[0];
        Assert.Equal(1, t1.Number);
        Assert.Equal("Song 1", t1.Title);
        Assert.Equal(TimeSpan.Zero, t1.StartTime);
        Assert.Equal(TimeSpan.FromMinutes(3), t1.Duration);

        var t2 = sheet.Files[0].Tracks[1];
        Assert.Equal(2, t2.Number);
        Assert.Equal("Song 2", t2.Title);
        Assert.Equal(TimeSpan.FromMinutes(3), t2.StartTime);
    }

    [Fact]
    public void ParseCue_WithTracksBeforeFile_HandlesLogic()
    {
        var content = """
            REM GENRE Test
            TITLE "Implicit File Album"
              TRACK 01 AUDIO
                TITLE "Implicit Track"
                INDEX 01 00:00:00
            """;
        
        var bytes = Encoding.UTF8.GetBytes(content);
        var fakePath = @"C:\Fake\Dir\Implicit.cue";
        var sheet = _cueService.ParseCue(bytes, fakePath);
        
        Assert.Single(sheet.Files);
        // The service should guess filename from the cue file path
        Assert.Equal("Implicit", sheet.Files[0].FileName);
        
        Assert.Single(sheet.Files[0].Tracks);
        Assert.Equal("Implicit Track", sheet.Files[0].Tracks[0].Title);
    }

    [Fact]
    public void ParseCue_WithMultipleFiles_ParsesAll()
    {
        var content = """
            TITLE "Multi Disc"
            FILE "CD1.wav" WAVE
              TRACK 01 AUDIO
                TITLE "Track 1"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Track 2"
                INDEX 01 02:00:00
            FILE "CD2.wav" WAVE
              TRACK 01 AUDIO
                TITLE "Track 3"
                INDEX 01 00:00:00
            """;
        
        var bytes = Encoding.UTF8.GetBytes(content);
        var sheet = _cueService.ParseCue(bytes, "multidisc.cue");

        Assert.Equal(2, sheet.Files.Count);
        
        Assert.Equal("CD1.wav", sheet.Files[0].FileName);
        Assert.Equal(2, sheet.Files[0].Tracks.Count);
        Assert.Null(sheet.Files[0].Tracks[1].Duration); 

        Assert.Equal("CD2.wav", sheet.Files[1].FileName);
        Assert.Single(sheet.Files[1].Tracks);
    }

    [Fact]
    public void VerifyAllRealCueFiles_InMusicDirectory_CanBeParsed()
    {
        // This is the integration test with user's real data
        // Uses the file path overload which reads from disk
        if (!Directory.Exists(_musicDir))
        {
            _output.WriteLine($"Music directory not found at: {_musicDir}. Skipping integration test.");
            return;
        }

        var cueFiles = Directory.GetFiles(_musicDir, "*.cue");
        _output.WriteLine($"Found {cueFiles.Length} CUE files in {_musicDir}.");
        
        foreach (var cueFile in cueFiles)
        {
            _output.WriteLine($"Testing: {Path.GetFileName(cueFile)}");
            try
            {
                var sheet = _cueService.ParseCue(cueFile);
                Assert.NotNull(sheet);
                Assert.True(sheet.Files.Count > 0, $"Real file {cueFile} should have detected files.");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"FAILED to parse {cueFile}: {ex}");
                throw;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lucene.Net.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using repos.Services;
using Xunit;
using Xunit.Abstractions;

namespace PurewwkTests;

public class LuceneServiceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly LuceneService _luceneService;
    private readonly Mock<IConfiguration> _configMock;
    private readonly MemoryFileSystem _fileSystem;
    private readonly RAMDirectory _ramDirectory;
    private readonly string _musicRoot = @"C:\Music";

    public LuceneServiceTests(ITestOutputHelper output)
    {
        _output = output;
        
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _fileSystem = new MemoryFileSystem();
        _fileSystem.AddDirectory(_musicRoot);

        _configMock = new Mock<IConfiguration>();
        _configMock.Setup(c => c["WorkingDirectory"]).Returns(@"C:\App");

        // Dependencies
        var cueService = new CueService(NullLogger<CueService>.Instance, _fileSystem);
        var cueFolderService = new CueFolderService(NullLogger<CueFolderService>.Instance, cueService);
        
        _ramDirectory = new RAMDirectory();

        _luceneService = new LuceneService(
            NullLogger<LuceneService>.Instance,
            _configMock.Object,
            cueService,
            cueFolderService,
            _fileSystem,
            _ramDirectory
        );
    }

    public void Dispose()
    {
        _luceneService.Dispose();
        _ramDirectory.Dispose();
    }

    [Fact]
    public void IsIndexValid_InitiallyFalse_ThenTrueAfterIndexing()
    {
        // For RAMDirectory, IsIndexValid checks if there are docs.
        Assert.False(_luceneService.IsIndexValid(), "Index should be invalid initially (0 docs)");

        // Add a file
        var artistDir = Path.Combine(_musicRoot, "Simple");
        _fileSystem.AddDirectory(artistDir);
        _fileSystem.AddFile(Path.Combine(artistDir, "test.mp3"), "content");
        
        _luceneService.IndexDirectory(_musicRoot);
        
        Assert.True(_luceneService.IsIndexValid(), "Index should be valid after indexing content");
    }

    [Fact]
    public void IndexDirectory_WithNormalFiles_IndexesCorrectly()
    {
        var artistDir = Path.Combine(_musicRoot, "TestArtist");
        var albumDir = Path.Combine(artistDir, "TestAlbum");
        _fileSystem.AddDirectory(artistDir);
        _fileSystem.AddDirectory(albumDir);

        _fileSystem.AddFile(Path.Combine(albumDir, "song1.mp3"), "dummy content");
        _fileSystem.AddFile(Path.Combine(albumDir, "song2.flac"), "dummy content");
        _fileSystem.AddFile(Path.Combine(albumDir, "cover.jpg"), "dummy content");

        _luceneService.IndexDirectory(_musicRoot);

        var rootChildren = _luceneService.GetChildren("/"); 
        var artistNode = rootChildren.FirstOrDefault(x => x["name"] == "TestArtist");
        Assert.NotNull(artistNode);
        Assert.Equal("true", artistNode["isDir"]);

        var artistId = artistNode["id"];
        var albumChildren = _luceneService.GetChildren(artistId);
        var albumNode = albumChildren.FirstOrDefault(x => x["name"] == "TestAlbum");
        Assert.NotNull(albumNode);

        var albumId = albumNode["id"];
        var songs = _luceneService.GetChildren(albumId);
        
        Assert.Equal(2, songs.Count);
        Assert.Contains(songs, s => s["name"] == "song1.mp3");
        Assert.Contains(songs, s => s["name"] == "song2.flac");
        Assert.DoesNotContain(songs, s => s["name"] == "cover.jpg");
    }

    [Fact]
    public void IndexDirectory_WithCueFile_IndexesAsDirectoryAndTracks()
    {
        var albumDir = Path.Combine(_musicRoot, "CueAlbum");
        _fileSystem.AddDirectory(albumDir);

        var cueContent = """
            PERFORMER "Cue Artist"
            TITLE "Cue Album"
            FILE "source.wav" WAVE
              TRACK 01 AUDIO
                TITLE "Track 1"
                PERFORMER "Cue Artist"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Track 2"
                PERFORMER "Cue Artist"
                INDEX 01 03:00:00
            """;
        
        _fileSystem.AddFile(Path.Combine(albumDir, "album.cue"), cueContent);
        _fileSystem.AddFile(Path.Combine(albumDir, "source.wav"), "dummy audio");

        _luceneService.IndexDirectory(_musicRoot);

        var rootChildren = _luceneService.GetChildren("/");
        var albumNode = rootChildren.FirstOrDefault(x => x["name"] == "CueAlbum");
        Assert.NotNull(albumNode);
        
        var items = _luceneService.GetChildren(albumNode["id"]);
        var cueNode = items.FirstOrDefault(x => x["name"] == "album.cue");
        Assert.NotNull(cueNode);
        Assert.Equal("true", cueNode["isDir"]);

        var tracks = _luceneService.GetChildren(cueNode["id"]);
        Assert.Equal(2, tracks.Count);
        
        var t1 = tracks.FirstOrDefault(x => x["title"] == "Track 1");
        Assert.NotNull(t1);
        Assert.Equal("true", t1["isCueTrack"]);
        
        Assert.DoesNotContain(items, x => x["name"] == "source.wav");
    }

    [Fact]
    public void AddOrUpdatePath_AddsNewFile_Correctly()
    {
        _luceneService.IndexDirectory(_musicRoot);
        
        var newFile = Path.Combine(_musicRoot, "new_song.mp3");
        _fileSystem.AddFile(newFile, "content");
        
        _luceneService.AddOrUpdatePath(newFile, _musicRoot);
        
        var children = _luceneService.GetChildren("/");
        Assert.Contains(children, x => x["name"] == "new_song.mp3");
    }

    [Fact]
    public void RemoveFromIndex_RemovesFile_Correctly()
    {
         var file = Path.Combine(_musicRoot, "remove.mp3");
         _fileSystem.AddFile(file, "content");
         _luceneService.IndexDirectory(_musicRoot);
         
         Assert.Contains(_luceneService.GetChildren("/"), x => x["name"] == "remove.mp3");
         
         _luceneService.RemoveFromIndex(file);
         
         Assert.DoesNotContain(_luceneService.GetChildren("/"), x => x["name"] == "remove.mp3");
    }
}

// In-Memory implementation of IFileSystem
public class MemoryFileSystem : IFileSystem
{
    private Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public void AddFile(string path, string content) => AddFile(path, Encoding.UTF8.GetBytes(content));
    public void AddFile(string path, byte[] content)
    {
        _files[path] = content;
        var dir = Path.GetDirectoryName(path);
        if (dir != null) AddDirectory(dir);
    }

    public void AddDirectory(string path)
    {
        if (_directories.Contains(path)) return;
        _directories.Add(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) AddDirectory(parent);
    }

    public bool DirectoryExists(string path) => _directories.Contains(path);
    public bool FileExists(string path) => _files.ContainsKey(path);
    
    public IEnumerable<string> GetFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        // Simple search pattern support (matches all or exact extension if trivial, but here assuming * or *.cue)
        // Implementing simple glob logic
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(searchPattern).Replace("\\*", ".*") + "$";
        var regex = new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return _files.Keys.Where(f => 
        {
            if (Path.GetDirectoryName(f)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (searchOption == SearchOption.TopDirectoryOnly) return regex.IsMatch(Path.GetFileName(f));
                return false; // logic for TopDirectoryOnly
            }
            if (searchOption == SearchOption.AllDirectories)
            {
                if (f.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return regex.IsMatch(Path.GetFileName(f));
            }
            return false;
        });
    }

    public IEnumerable<string> GetDirectories(string path)
    {
        return _directories.Where(d => 
        {
            var parent = Path.GetDirectoryName(d);
            return parent != null && parent.Equals(path, StringComparison.OrdinalIgnoreCase);
        });
    }

    public IFileSystemDirectoryInfo GetDirectoryInfo(string path)
    {
        if (!_directories.Contains(path)) throw new DirectoryNotFoundException(path);
        return new MemoryFileSystemDirectoryInfo(path, this);
    }

    public string GetExtension(string path) => Path.GetExtension(path);
    public string GetFileName(string path) => Path.GetFileName(path);
    public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? "";
    public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);
    public byte[] ReadAllBytes(string path)
    {
        if (_files.TryGetValue(path, out var bytes)) return bytes;
        throw new FileNotFoundException(path);
    }
}

public class MemoryFileSystemDirectoryInfo : IFileSystemDirectoryInfo
{
    private string _path;
    private MemoryFileSystem _fs;

    public MemoryFileSystemDirectoryInfo(string path, MemoryFileSystem fs)
    {
        _path = path;
        _fs = fs;
    }

    public string Name => Path.GetFileName(_path);
    public string FullName => _path;

    public IEnumerable<IFileSystemDirectoryInfo> GetDirectories()
    {
        return _fs.GetDirectories(_path).Select(d => new MemoryFileSystemDirectoryInfo(d, _fs));
    }

    public IEnumerable<IFileSystemFileInfo> GetFiles()
    {
        return _fs.GetFiles(_path).Select(f => new MemoryFileSystemFileInfo(f, _fs));
    }
    
    public IEnumerable<IFileSystemFileInfo> GetFiles(string searchPattern)
    {
        return _fs.GetFiles(_path, searchPattern).Select(f => new MemoryFileSystemFileInfo(f, _fs));
    }
}

public class MemoryFileSystemFileInfo : IFileSystemFileInfo
{
    private string _path;
    private MemoryFileSystem _fs;

    public MemoryFileSystemFileInfo(string path, MemoryFileSystem fs)
    {
        _path = path;
        _fs = fs;
    }

    public string Name => Path.GetFileName(_path);
    public string FullName => _path;
    public string Extension => Path.GetExtension(_path);
    public bool Exists => _fs.FileExists(_path);
}

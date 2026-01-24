using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;

namespace repos.Services;

public class LuceneService : IDisposable
{
    private readonly ILogger<LuceneService> _logger;
    private readonly CueService _cueService;
    private readonly CueFolderService _cueFolderService;
    private readonly string _indexPath;
    private readonly IFileSystem _fileSystem;
    private readonly Lucene.Net.Store.Directory _directory;
    private readonly Lucene.Net.Analysis.Analyzer _analyzer;
    private readonly IndexWriter _writer;
    private readonly LuceneVersion _version = LuceneVersion.LUCENE_48;
    private readonly string[] _musicExtensions = [".mp3", ".flac", ".wav", ".ogg", ".mp4", ".m4a", ".aac", ".wma", ".cue"];
    // Supported audio extensions for CUE source file detection
    private readonly string[] _audioExtensions = [".mp3", ".flac", ".wav", ".ape", ".wv", ".m4a", ".tta", ".tak"];

    public LuceneService(ILogger<LuceneService> logger, IConfiguration configuration, CueService cueService, CueFolderService cueFolderService, IFileSystem fileSystem)
        : this(logger, configuration, cueService, cueFolderService, fileSystem, null)
    {
    }

    public LuceneService(ILogger<LuceneService> logger, IConfiguration configuration, CueService cueService, CueFolderService cueFolderService, IFileSystem fileSystem, Lucene.Net.Store.Directory? luceneDirectory = null)
    {
        _logger = logger;
        _cueService = cueService;
        _cueFolderService = cueFolderService;
        _fileSystem = fileSystem;
        
        var workingDir = configuration["WorkingDirectory"];
        var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
        var indexPath = Path.Combine(baseDir, "music_index");
        
        _indexPath = indexPath;
        
        if (luceneDirectory != null)
        {
            _directory = luceneDirectory;
        }
        else
        {
            _directory = Lucene.Net.Store.FSDirectory.Open(indexPath);
        }

        _analyzer = new StandardAnalyzer(_version);
        var config = new IndexWriterConfig(_version, _analyzer);
        _writer = new IndexWriter(_directory, config);
    }    public bool IsIndexValid()
    {
        try
        {
            // If we are using a RAMDirectory or mocked directory, we assume it's valid if injected.
            // But if it's FSDirectory check the path.
            if (_directory is Lucene.Net.Store.FSDirectory fsDir)
            {
                 if (!_fileSystem.DirectoryExists(fsDir.Directory.FullName))
                 {
                     return false;
                 }
                 if (!_fileSystem.GetFiles(fsDir.Directory.FullName, "*", SearchOption.AllDirectories).Any())
                 {
                     return false;
                 }
            }

            // Try to open and read the index
            using var reader = DirectoryReader.Open(_directory);
            return reader.NumDocs > 0; // Return true if index has documents
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index validation failed: {Message}", ex.Message);
            return false;
        }
    }

    public void ClearIndex()
    {
        _writer.DeleteAll();
        _writer.Commit();
        _logger.LogInformation("Index cleared");
    }

    public void RemoveFromIndex(string path)
    {
        // パスに基づいてドキュメントを削除
        var query = new TermQuery(new Term("path", path));
        _writer.DeleteDocuments(query);
        
        // 該当パスで始まる子要素も削除（ディレクトリの場合）
        if (_fileSystem.DirectoryExists(path))
        {
            var prefixQuery = new PrefixQuery(new Term("path", path + Path.DirectorySeparatorChar));
            _writer.DeleteDocuments(prefixQuery);
        }
        
        _writer.Commit();
        _logger.LogDebug("Removed from index: {Path}", path);
    }

    private bool IsMusicFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var isMusic = _musicExtensions.Contains(extension);
        if (!isMusic) {
             _logger.LogInformation("File rejected by IsMusicFile: {Path}, Ext: {Ext}", filePath, extension);
        }
        return isMusic;
    }

    private bool DirectoryContainsMusicFiles(string dirPath)
    {
        try
        {
            _logger.LogInformation("Checking directory for music: {DirPath}", dirPath);
            var files = _fileSystem.GetFiles(dirPath).ToArray();
            _logger.LogInformation("Found {Count} files in {DirPath}", files.Length, dirPath);
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                _logger.LogInformation("File in check: {Name}, Ext: {Ext}, IsMusic: {IsMusic}", Path.GetFileName(f), ext, IsMusicFile(f));
            }

            // Check if directory has any music files directly
            if (files.Any(file => IsMusicFile(file)))
            {
                return true;
            }

            // Recursively check subdirectories
            return _fileSystem.GetDirectories(dirPath).Any(subDir => DirectoryContainsMusicFiles(subDir));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking directory: {DirPath}", dirPath);
            return false;
        }
    }

    public void AddOrUpdatePath(string fullPath, string musicRootPath)
    {
        try
        {
            // Convert full path to relative ID
            var relativePath = Path.GetRelativePath(musicRootPath, fullPath).Replace('\\', '/');
            var id = "/" + relativePath;
            
            // Remove existing entry if it exists
            var query = new TermQuery(new Term("path", fullPath));
            _writer.DeleteDocuments(query);
            
            if (_fileSystem.DirectoryExists(fullPath))
            {
                // Only index directory if it contains music files
                if (DirectoryContainsMusicFiles(fullPath))
                {
                    var parentPath = Path.GetDirectoryName(relativePath);
                    var parentId = string.IsNullOrEmpty(parentPath) ? "/" : "/" + parentPath.Replace('\\', '/');
                    
                    var doc = new Document
                    {
                        new StringField("id", id, Field.Store.YES),
                        new StringField("parent", parentId, Field.Store.YES),
                        new StringField("isDir", "true", Field.Store.YES),
                        new StringField("title", Path.GetFileName(fullPath), Field.Store.YES),
                        new StringField("name", Path.GetFileName(fullPath), Field.Store.YES),
                        new StringField("path", id, Field.Store.YES)
                    };
                    _writer.AddDocument(doc);
                    
                    // Index subdirectories and files
                    IndexDirectoryIncremental(fullPath, id, musicRootPath);
                }
                else
                {
                    _logger.LogDebug("Skipping directory (no music files): {Path}", fullPath);
                }
            }
            else if (_fileSystem.FileExists(fullPath) && IsMusicFile(fullPath))
            {
                var dirPath = Path.GetDirectoryName(fullPath);
                var shouldIndex = true;
                if (dirPath != null)
                {
                    GetCueSuppressionInfo(dirPath, out var validCues, out var suppressedFiles);
                    if (fullPath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!validCues.Contains(fullPath)) shouldIndex = false;
                    }
                    else
                    {
                        if (suppressedFiles.Contains(fullPath)) shouldIndex = false;
                    }
                }

                if (shouldIndex)
                {
                    var parentPath = Path.GetDirectoryName(relativePath);
                    var parentId = string.IsNullOrEmpty(parentPath) ? "/" : "/" + parentPath.Replace('\\', '/');

                    if (fullPath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                    {
                        // CUE file: Index as directory and add tracks
                        var doc = new Document
                        {
                            new StringField("id", id, Field.Store.YES),
                            new StringField("parent", parentId, Field.Store.YES),
                            new StringField("isDir", "true", Field.Store.YES),
                            new StringField("title", Path.GetFileName(fullPath), Field.Store.YES),
                            new StringField("name", Path.GetFileName(fullPath), Field.Store.YES),
                            new StringField("path", fullPath, Field.Store.YES)
                        };
                        _writer.AddDocument(doc);
                        
                        // Re-index tracks
                        IndexCueTracks(fullPath, id);
                    }
                    else
                    {
                        // Normal music file
                        var doc = new Document
                        {
                            new StringField("id", id, Field.Store.YES),
                            new StringField("parent", parentId, Field.Store.YES),
                            new StringField("isDir", "false", Field.Store.YES),
                            new StringField("title", Path.GetFileName(fullPath), Field.Store.YES),
                            new StringField("artist", "", Field.Store.YES),
                            new StringField("coverArt", "", Field.Store.YES),
                            new StringField("name", Path.GetFileName(fullPath), Field.Store.YES),
                            new StringField("path", fullPath, Field.Store.YES)
                        };
                        _writer.AddDocument(doc);
                    }
                }
                else
                {
                     _logger.LogInformation("Skipping suppressed or invalid file: {Path}", fullPath);
                }
            }
            else if (_fileSystem.FileExists(fullPath))
            {
                _logger.LogDebug("Skipping non-music file: {Path}", fullPath);
            }
            
            _writer.Commit();
            _logger.LogDebug("Added/Updated in index: {Path}", fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add/update path in index: {Path}, Error: {Message}", fullPath, ex.Message);
        }
    }

    private void GetCueSuppressionInfo(string dirPath, out HashSet<string> validCues, out HashSet<string> suppressedFiles)
    {
        validCues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        suppressedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var dirInfo = _fileSystem.GetDirectoryInfo(dirPath);
            var cues = dirInfo.GetFiles("*.cue");

            foreach (var cueFile in cues)
            {
                try
                {
                    var sheet = _cueService.ParseCue(cueFile.FullName);
                    bool valid = true;
                    var currentSuppressed = new List<string>();

                    if (sheet.Files.Count == 0) valid = false;

                    foreach (var file in sheet.Files)
                    {
                        var audioPath = _cueService.ResolveAudioFile(cueFile.FullName, file.FileName);
                        if (audioPath != null)
                        {
                            currentSuppressed.Add(audioPath);
                        }
                    }

                    // If we have files defined but none resolved, consider it invalid (empty/broken CUE)
                    if (sheet.Files.Count > 0 && currentSuppressed.Count == 0)
                    {
                        valid = false;
                    }

                    if (valid)
                    {
                        validCues.Add(cueFile.FullName);
                        foreach (var s in currentSuppressed) suppressedFiles.Add(s);
                    }
                    else
                    {
                        _logger.LogWarning("CUE file incomplete (missing source), marking invalid: {Path}", cueFile.FullName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error checking CUE validity: {Path}", cueFile.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting CUE suppression info for: {Path}", dirPath);
        }
    }

    private void IndexDirectoryIncremental(string dirPath, string currentId, string musicRootPath)
        {
        var dirInfo = _fileSystem.GetDirectoryInfo(dirPath);
        foreach (var subDir in dirInfo.GetDirectories())
        {
            // Only index directories that contain music files
            if (DirectoryContainsMusicFiles(subDir.FullName))
            {
                var relativePath = Path.GetRelativePath(musicRootPath, subDir.FullName).Replace('\\', '/');
                var subDirId = "/" + relativePath;
                
                var doc = new Document
                {
                    new StringField("id", subDirId, Field.Store.YES),
                    new StringField("parent", currentId, Field.Store.YES),
                    new StringField("isDir", "true", Field.Store.YES),
                    new StringField("title", subDir.Name, Field.Store.YES),
                    new StringField("name", subDir.Name, Field.Store.YES),
                    new StringField("path", subDirId, Field.Store.YES)
                };
                _writer.AddDocument(doc);
                IndexDirectoryIncremental(subDir.FullName, subDirId, musicRootPath);
            }
        }
        
        GetCueSuppressionInfo(dirPath, out var validCues, out var suppressedFiles);

        foreach (var file in dirInfo.GetFiles())
        {
            // Only index music files
            if (IsMusicFile(file.FullName))
            {
                if (suppressedFiles.Contains(file.FullName))
                {
                    _logger.LogDebug("Skipping suppressed file: {Path}", file.FullName);
                    continue;
                }

                var isCue = file.Extension.Equals(".cue", StringComparison.OrdinalIgnoreCase);
                if (isCue && !validCues.Contains(file.FullName))
                {
                    _logger.LogDebug("Skipping invalid CUE: {Path}", file.FullName);
                    continue;
                }

                var relativePath = Path.GetRelativePath(musicRootPath, file.FullName).Replace('\\', '/');
                var fileId = "/" + relativePath;
                
                if (isCue)
                {
                    _logger.LogInformation("Found CUE file, indexing as directory: {FileName}", file.Name);
                    var doc = new Document
                    {
                        new StringField("id", fileId, Field.Store.YES),
                        new StringField("parent", currentId, Field.Store.YES),
                        new StringField("isDir", "true", Field.Store.YES),
                        new StringField("title", file.Name, Field.Store.YES),
                        new StringField("name", file.Name, Field.Store.YES),
                        new StringField("path", file.FullName, Field.Store.YES)
                    };
                    _writer.AddDocument(doc);
                    IndexCueTracks(file.FullName, fileId);
                }
                else
                {
                    var doc = new Document
                    {
                        new StringField("id", fileId, Field.Store.YES),
                        new StringField("parent", currentId, Field.Store.YES),
                        new StringField("isDir", "false", Field.Store.YES),
                        new StringField("title", file.Name, Field.Store.YES),
                        new StringField("artist", "", Field.Store.YES),
                        new StringField("coverArt", "", Field.Store.YES),
                        new StringField("name", file.Name, Field.Store.YES),
                        new StringField("path", file.FullName, Field.Store.YES)
                    };
                    _writer.AddDocument(doc);
                }
            }
        }
    }

    private volatile bool _isScanning = false;
    private readonly object _scanLock = new object();

    public void IndexDirectory(string dirPath, string? parentId = null)
    {
        lock (_scanLock)
        {
            // Only allow one root-level scan at a time
            if (parentId == null)
            {
                if (_isScanning)
                {
                    _logger.LogWarning("Scan already in progress, skipping request");
                    return;
                }
                _isScanning = true;
            }

            try
            {
                _logger.LogInformation("Indexing directory: {Path} with parentId: {ParentId}", dirPath, parentId);
                var currentId = parentId ?? "/";
                if (parentId == null)
                {
                    // root directory - 完全再インデックスの場合のみクリア
                    ClearIndex();
                    var doc = new Document
                    {
                        new StringField("id", "/", Field.Store.YES),
                        new StringField("parent", "", Field.Store.YES),
                        new StringField("isDir", "true", Field.Store.YES),
                        new StringField("title", Path.GetFileName(dirPath), Field.Store.YES),
                        new StringField("name", Path.GetFileName(dirPath), Field.Store.YES),
                        new StringField("path", "/", Field.Store.YES)
                    };
                    _writer.AddDocument(doc);
                }
                var dirInfo = _fileSystem.GetDirectoryInfo(dirPath);
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    // Only index directories that contain music files
                    if (DirectoryContainsMusicFiles(subDir.FullName))
                    {
                        var subDirId = $"{currentId.TrimEnd('/')}/{subDir.Name}";
                        _logger.LogDebug("Indexing subdir: {SubDirName} id: {SubDirId}", subDir.Name, subDirId);
                        var doc = new Document
                        {
                            new StringField("id", subDirId, Field.Store.YES),
                            new StringField("parent", currentId, Field.Store.YES),
                            new StringField("isDir", "true", Field.Store.YES),
                            new StringField("title", subDir.Name, Field.Store.YES),
                            new StringField("name", subDir.Name, Field.Store.YES),
                            new StringField("path", subDirId, Field.Store.YES)
                        };
                        _writer.AddDocument(doc);
                        // 再帰的にサブディレクトリをインデックス
                        IndexDirectory(subDir.FullName, subDirId);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping directory (no music files): {SubDirName}", subDir.Name);
                    }
                }
                GetCueSuppressionInfo(dirPath, out var validCues, out var suppressedFiles);

                foreach (var file in dirInfo.GetFiles())
                {
                    try
                    {
                        var isCue = file.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase);
                        // Check IsMusicFile OR isCue fallback
                        if (IsMusicFile(file.FullName) || isCue)
                        {
                            if (suppressedFiles.Contains(file.FullName))
                            {
                                _logger.LogDebug("Skipping suppressed file: {Path}", file.FullName);
                                continue;
                            }

                            if (isCue && !validCues.Contains(file.FullName))
                            {
                                _logger.LogDebug("Skipping invalid CUE: {Path}", file.FullName);
                                continue;
                            }

                            var fileId = $"{currentId.TrimEnd('/')}/{file.Name}";

                            if (isCue)
                            {
                                _logger.LogInformation("Indexing CUE file as directory: {FileName}", file.Name);
                                var doc = new Document
                                {
                                    new StringField("id", fileId, Field.Store.YES),
                                    new StringField("parent", currentId, Field.Store.YES),
                                    new StringField("isDir", "true", Field.Store.YES), // CUE file acts as directory
                                    new StringField("title", file.Name, Field.Store.YES),
                                    new StringField("name", file.Name, Field.Store.YES),
                                    new StringField("path", file.FullName, Field.Store.YES)
                                };
                                _writer.AddDocument(doc);
                                IndexCueTracks(file.FullName, fileId);
                            }
                            else
                            {
                                // Normal music file
                                var doc = new Document
                                {
                                    new StringField("id", fileId, Field.Store.YES),
                                    new StringField("parent", currentId, Field.Store.YES),
                                    new StringField("isDir", "false", Field.Store.YES),
                                    new StringField("title", file.Name, Field.Store.YES),
                                    new StringField("artist", "", Field.Store.YES),
                                    new StringField("coverArt", "", Field.Store.YES),
                                    new StringField("name", file.Name, Field.Store.YES),
                                    new StringField("path", file.FullName, Field.Store.YES)
                                };
                                _writer.AddDocument(doc);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing file: {FileName}", file.Name);
                    }
                }
                
                if (parentId == null)
                {
                    _writer.Commit();
                }
            }
            finally
            {
                if (parentId == null)
                {
                    _isScanning = false;
                }
            }
        }
    }

    private void IndexCueTracks(string cueFilePath, string parentId)
    {
        try
        {
            var tracks = _cueFolderService.GetVirtualTracks(cueFilePath);
            foreach (var track in tracks)
            {
                var virtualId = $"{parentId}/{track.TrackNumber:00}";
                var doc = new Document
                {
                    new StringField("id", virtualId, Field.Store.YES),
                    new StringField("parent", parentId, Field.Store.YES),
                    new StringField("isDir", "false", Field.Store.YES),
                    new StringField("title", track.Title, Field.Store.YES),
                    new StringField("artist", track.Artist, Field.Store.YES),
                    new StringField("coverArt", "", Field.Store.YES),
                    new StringField("name", track.VirtualFileName, Field.Store.YES),
                    new StringField("path", track.SourceAudioPath, Field.Store.YES),
                    new StringField("isCueTrack", "true", Field.Store.YES),
                    new DoubleField("cueStart", track.StartTime.TotalSeconds, Field.Store.YES),
                    new DoubleField("cueDuration", track.Duration?.TotalSeconds ?? 0, Field.Store.YES)
                };
                _writer.AddDocument(doc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index CUE tracks: {Path}", cueFilePath);
        }
    }

    // ParseCue method removed (moved to CueService)

    public Dictionary<string, string>? GetDocumentById(string id)
    {
        try
        {
            using var reader = DirectoryReader.Open(_directory);
            var searcher = new IndexSearcher(reader);
            var query = new TermQuery(new Term("id", id));
            var hits = searcher.Search(query, 1).ScoreDocs;
            if (hits.Length == 0) return null;

            var doc = searcher.Doc(hits[0].Doc);
            var dict = new Dictionary<string, string>();
            foreach (var field in doc.Fields)
            {
                dict[field.Name] = field.GetStringValue();
            }
            return dict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting document by id: {Id}", id);
            return null;
        }
    }

    public List<Dictionary<string, string>> GetChildren(string parentId)
    {
        try
        {
            using var reader = DirectoryReader.Open(_directory);
            var searcher = new IndexSearcher(reader);
            var query = new TermQuery(new Term("parent", parentId));
            var hits = searcher.Search(query, 1000).ScoreDocs;
            var result = new List<Dictionary<string, string>>();
        foreach (var hit in hits)
        {
            var doc = searcher.Doc(hit.Doc);
            var dict = new Dictionary<string, string>();
            foreach (var field in doc.Fields)
            {
                dict[field.Name] = field.GetStringValue();
            }
            result.Add(dict);
        }
        
        // Sort results by filename: directories first, then files, both sorted in natural order
        return result.OrderBy(item => 
        {
            var isDir = item.ContainsKey("isDir") && item["isDir"] == "true";
            var name = item.ContainsKey("name") ? item["name"] : "";
            // Directories come first (0), then files (1)
            var prefix = isDir ? "0" : "1";
            return prefix;
        }).ThenBy(item => 
        {
            var name = item.ContainsKey("name") ? item["name"] : "";
            return name;
        }, new NaturalStringComparer()).ToList();
        }
        catch (IndexNotFoundException)
        {
            // Index doesn't exist yet, return empty list
            _logger.LogDebug("Index not found, returning empty result");
            return new List<Dictionary<string, string>>();
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _directory?.Dispose();
        _analyzer?.Dispose();
    }
}

public class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int xIndex = 0, yIndex = 0;

        while (xIndex < x.Length && yIndex < y.Length)
        {
            // Extract numeric or non-numeric parts
            var xPart = ExtractPart(x, ref xIndex);
            var yPart = ExtractPart(y, ref yIndex);

            // If both parts are numeric, compare as numbers
            if (long.TryParse(xPart, out long xNum) && long.TryParse(yPart, out long yNum))
            {
                int numComparison = xNum.CompareTo(yNum);
                if (numComparison != 0) return numComparison;
            }
            // Otherwise, compare as strings (case-insensitive)
            else
            {
                int stringComparison = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                if (stringComparison != 0) return stringComparison;
            }
        }

        // If one string is longer than the other
        return x.Length.CompareTo(y.Length);
    }

    private static string ExtractPart(string str, ref int index)
    {
        if (index >= str.Length) return "";

        var start = index;
        var isDigit = char.IsDigit(str[index]);

        // Extract consecutive digits or non-digits
        while (index < str.Length && char.IsDigit(str[index]) == isDigit)
        {
            index++;
        }

        return str.Substring(start, index - start);
    }
}

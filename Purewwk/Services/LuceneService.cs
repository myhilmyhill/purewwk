using Purewwk.Plugin;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Extensions.Options;
using Purewwk.Models;

namespace Purewwk.Services;

public class LuceneService : ILuceneService, IDisposable
{
    private readonly ILogger<LuceneService> _logger;
    private readonly IEnumerable<IIndexingPlugin> _indexingPlugins;
    private readonly IEnumerable<IPlayablePlugin> _playbackPlugins;
    private readonly IFileSystem _fileSystem;
    private readonly Lucene.Net.Store.Directory _directory;
    private readonly Lucene.Net.Analysis.Analyzer _analyzer;
    private readonly IndexWriter _writer;
    private readonly LuceneVersion _version = LuceneVersion.LUCENE_48;
    private readonly string _indexPath;

    public LuceneService(ILogger<LuceneService> logger, IOptions<PurewwkConfig> config, IEnumerable<IIndexingPlugin> indexingPlugins, IEnumerable<IPlayablePlugin> playbackPlugins, IFileSystem fileSystem)
        : this(logger, config, indexingPlugins, playbackPlugins, fileSystem, null)
    {
    }

    public LuceneService(ILogger<LuceneService> logger, IOptions<PurewwkConfig> config, IEnumerable<IIndexingPlugin> indexingPlugins, IEnumerable<IPlayablePlugin> playbackPlugins, IFileSystem fileSystem, Lucene.Net.Store.Directory? luceneDirectory = null)
    {
        _logger = logger;
        _indexingPlugins = indexingPlugins;
        _playbackPlugins = playbackPlugins;
        _fileSystem = fileSystem;
        
        var workingDir = config.Value.WorkingDirectory;
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
        var indexWriterConfig = new IndexWriterConfig(_version, _analyzer);
        _writer = new IndexWriter(_directory, indexWriterConfig);
    }
    
    public bool IsIndexValid()
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
        var query = new TermQuery(new Term("path", path));
        _writer.DeleteDocuments(query);
        
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
        return _indexingPlugins.Any(p => p.CanHandle(extension)) || _playbackPlugins.Any(p => p.CanHandle(extension));
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
                if (!IsFileSuppressed(fullPath))
                {
                    var parentPath = Path.GetDirectoryName(relativePath);
                    var parentId = string.IsNullOrEmpty(parentPath) ? "/" : "/" + parentPath.Replace('\\', '/');
                    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                    var provider = _indexingPlugins.FirstOrDefault(p => p.CanHandle(ext));

                    if (provider != null)
                    {
                        // Index as directory using relevant provider
                        var doc = new Document
                        {
                            new StringField("id", id, Field.Store.YES),
                            new StringField("parent", parentId, Field.Store.YES),
                            new StringField("isDir", "true", Field.Store.YES),
                            new StringField("title", Path.GetFileName(fullPath), Field.Store.YES),
                            new StringField("path", fullPath, Field.Store.YES)
                        };
                        _writer.AddDocument(doc);
                        IndexVirtualTracks(fullPath, id, provider);
                    }
                    else
                    {
                        // Normal music file
                        var player = _playbackPlugins.FirstOrDefault(p => p.CanHandle(ext));
                        
                        var doc = new Document
                        {
                            new StringField("id", id, Field.Store.YES),
                            new StringField("parent", parentId, Field.Store.YES),
                            new StringField("isDir", "false", Field.Store.YES),
                            new StringField("title", Path.GetFileName(fullPath), Field.Store.YES),
                            new StringField("path", fullPath, Field.Store.YES),
                            new StringField("playerType", player?.GetPlayerType(ext) ?? "", Field.Store.YES)
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

    private void RegisterSuppressedFile(string suppressedPath, string sourcePath)
    {
        var query = new TermQuery(new Term("path", suppressedPath));
        _writer.DeleteDocuments(query);

        var doc = new Document
        {
            new StringField("type", "suppression", Field.Store.YES),
            new StringField("suppressedPath", suppressedPath, Field.Store.YES),
            new StringField("sourcePath", sourcePath, Field.Store.YES)
        };
        _writer.AddDocument(doc);

        if (_isScanning)
        {
            _scanSuppressedPaths.Add(suppressedPath);
        }
    }

    private bool IsFileSuppressed(string fullPath)
    {
        if (_isScanning && _scanSuppressedPaths.Contains(fullPath)) 
            return true;

        try
        {
            using var reader = DirectoryReader.Open(_writer, applyAllDeletes: true);
            var searcher = new IndexSearcher(reader);
            var query = new TermQuery(new Term("suppressedPath", fullPath));
            var hits = searcher.Search(query, 1).ScoreDocs;
            return hits.Length > 0;
        }
        catch
        {
            return false;
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
                    new StringField("path", subDirId, Field.Store.YES)
                };
                _writer.AddDocument(doc);
                IndexDirectoryIncremental(subDir.FullName, subDirId, musicRootPath);
            }
        }
        
        foreach (var file in dirInfo.GetFiles())
        {
            // Only index music files
            if (IsMusicFile(file.FullName))
            {
                if (IsFileSuppressed(file.FullName))
                {
                    _logger.LogDebug("Skipping suppressed file: {Path}", file.FullName);
                    continue;
                }

                var ext = file.Extension.ToLowerInvariant();
                var provider = _indexingPlugins.FirstOrDefault(p => p.CanHandle(ext));

                var relativePath = Path.GetRelativePath(musicRootPath, file.FullName).Replace('\\', '/');
                var fileId = "/" + relativePath;
                
                if (provider != null)
                {
                    _logger.LogInformation("Found specialized file, indexing as directory: {FileName}", file.Name);
                    var doc = new Document
                    {
                        new StringField("id", fileId, Field.Store.YES),
                        new StringField("parent", currentId, Field.Store.YES),
                        new StringField("isDir", "true", Field.Store.YES),
                        new StringField("title", file.Name, Field.Store.YES),
                        new StringField("path", file.FullName, Field.Store.YES)
                    };
                    _writer.AddDocument(doc);
                    IndexVirtualTracks(file.FullName, fileId, provider);
                }
                else
                {
                    var player = _playbackPlugins.FirstOrDefault(p => p.CanHandle(ext));

                    var doc = new Document
                    {
                        new StringField("id", fileId, Field.Store.YES),
                        new StringField("parent", currentId, Field.Store.YES),
                        new StringField("isDir", "false", Field.Store.YES),
                        new StringField("title", file.Name, Field.Store.YES),
                        new StringField("path", file.FullName, Field.Store.YES),
                        new StringField("playerType", player?.GetPlayerType(ext) ?? "", Field.Store.YES)
                    };
                    _writer.AddDocument(doc);
                }
            }
        }
    }

    private volatile bool _isScanning = false;
    private readonly object _scanLock = new object();
    private readonly HashSet<string> _scanSuppressedPaths = new(StringComparer.OrdinalIgnoreCase);

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
                _scanSuppressedPaths.Clear();
            }

            try
            {
                _logger.LogInformation("Indexing directory: {Path} with parentId: {ParentId}", dirPath, parentId);
                var currentId = parentId ?? "/";
                if (parentId == null)
                {
                    ClearIndex();
                    var doc = new Document
                    {
                        new StringField("id", "/", Field.Store.YES),
                        new StringField("parent", "", Field.Store.YES),
                        new StringField("isDir", "true", Field.Store.YES),
                        new StringField("title", Path.GetFileName(dirPath), Field.Store.YES),
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
                            new StringField("path", subDirId, Field.Store.YES)
                        };
                        _writer.AddDocument(doc);
                        IndexDirectory(subDir.FullName, subDirId);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping directory (no music files): {SubDirName}", subDir.Name);
                    }
                }
                foreach (var file in dirInfo.GetFiles())
                {
                    try
                    {
                        var ext = Path.GetExtension(file.FullName).ToLowerInvariant();
                        var indexingPlugin = _indexingPlugins.FirstOrDefault(p => p.CanHandle(ext));
                        var playbackPlugin = _playbackPlugins.FirstOrDefault(p => p.CanHandle(ext));

                        if (indexingPlugin != null || playbackPlugin != null)
                        {
                            if (IsFileSuppressed(file.FullName))
                            {
                                _logger.LogDebug("Skipping suppressed file: {Path}", file.FullName);
                                continue;
                            }

                            var fileId = $"{currentId.TrimEnd('/')}/{file.Name}";

                            if (indexingPlugin != null)
                            {
                                _logger.LogInformation("Indexing specialized file as directory: {FileName}", file.Name);
                                var doc = new Document
                                {
                                    new StringField("id", fileId, Field.Store.YES),
                                    new StringField("parent", currentId, Field.Store.YES),
                                    new StringField("isDir", "true", Field.Store.YES),
                                    new StringField("title", file.Name, Field.Store.YES),
                                    new StringField("path", file.FullName, Field.Store.YES)
                                };
                                _writer.AddDocument(doc);
                                IndexVirtualTracks(file.FullName, fileId, indexingPlugin);
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
                                    new StringField("path", file.FullName, Field.Store.YES),
                                    new StringField("playerType", playbackPlugin?.GetPlayerType(ext) ?? "", Field.Store.YES)
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

    private void IndexVirtualTracks(string filePath, string parentId, IIndexingPlugin provider)
    {
        try
        {
            var virtualFiles = provider.GetEntries(filePath);
            var handledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var vFile in virtualFiles)
            {
                if (!string.Equals(vFile.Path, filePath, StringComparison.OrdinalIgnoreCase) && handledPaths.Add(vFile.Path))
                {
                    RegisterSuppressedFile(vFile.Path, filePath);
                }

                var virtualId = $"{parentId}/{vFile.Id}";
                var doc = new Document
                {
                    new StringField("id", virtualId, Field.Store.YES),
                    new StringField("parent", parentId, Field.Store.YES),
                    new StringField("isDir", vFile.IsDir ? "true" : "false", Field.Store.YES),
                    new StringField("title", vFile.Title, Field.Store.YES),
                    new StringField("path", vFile.Path, Field.Store.YES)
                };

                if (vFile is MediaFile mediaFile)
                {
                    var extension = Path.GetExtension(mediaFile.Path).ToLowerInvariant();
                    if (mediaFile.StartTime.HasValue) doc.Add(new DoubleField("startTime", mediaFile.StartTime.Value, Field.Store.YES));
                    if (mediaFile.Duration.HasValue) doc.Add(new DoubleField("duration", mediaFile.Duration.Value, Field.Store.YES));
                    doc.Add(new StringField("playerType", mediaFile.Player.GetPlayerType(extension), Field.Store.YES));
                }

                _writer.AddDocument(doc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index virtual tracks for: {Path}", filePath);
        }
    }

    // ParseCue method removed (moved to CueService)

    public MediaItem? GetDocumentById(string id)
    {
        try
        {
            using var reader = DirectoryReader.Open(_directory);
            var searcher = new IndexSearcher(reader);
            var query = new TermQuery(new Term("id", id));
            var hits = searcher.Search(query, 1).ScoreDocs;
            if (hits.Length == 0) return null;

            var doc = searcher.Doc(hits[0].Doc);
            return DocumentToItem(doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting document by id: {Id}", id);
            return null;
        }
    }

    private MediaItem DocumentToItem(Document doc)
    {
        var isDir = doc.Get("isDir") == "true";
        var id = doc.Get("id") ?? "";
        var parent = doc.Get("parent") ?? "";
        var title = doc.Get("title") ?? "";
        var path = doc.Get("path") ?? "";

        if (isDir)
        {
            return new MediaFolder
            {
                Id = id,
                Parent = parent,
                Title = title,
                Path = path
            };
        }

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var playerTypeFromIndex = doc.Get("playerType") ?? "";

        // Prefer extension-based lookup, fallback to playerType for backwards compatibility if needed
        var player = _playbackPlugins.FirstOrDefault(p => p.CanHandle(ext));
        
        if (player == null && !string.IsNullOrEmpty(playerTypeFromIndex))
        {
            player = _playbackPlugins.FirstOrDefault(p => p.GetPlayerType("") == playerTypeFromIndex);
        }

        return new MediaFile
        {
            Id = id,
            Parent = parent,
            Title = title,
            Path = path,
            StartTime = doc.GetField("startTime")?.GetDoubleValue(),
            Duration = doc.GetField("duration")?.GetDoubleValue(),
            Player = player ?? throw new Exception($"Playable plugin is missing for {path} (ext: {ext})")
        };
    }

    public List<MediaItem> GetChildren(string parentId)
    {
        try
        {
            using var reader = DirectoryReader.Open(_directory);
            var searcher = new IndexSearcher(reader);
            var query = new TermQuery(new Term("parent", parentId));
            var hits = searcher.Search(query, 1000).ScoreDocs;
            var result = new List<MediaItem>();
        foreach (var hit in hits)
        {
            var doc = searcher.Doc(hit.Doc);
            result.Add(DocumentToItem(doc));
        }
        
        // Sort results by filename: directories first, then files, both sorted in natural order
        return result.OrderBy(item => 
        {
            // Directories come first (0), then files (1)
            var prefix = item.IsDir ? "0" : "1";
            return prefix;
        }).ThenBy(item =>  item.Title, new NaturalStringComparer()).ToList();
        }
        catch (IndexNotFoundException)
        {
            // Index doesn't exist yet, return empty list
            _logger.LogDebug("Index not found, returning empty result");
            return new List<MediaItem>();
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _directory?.Dispose();
        _analyzer?.Dispose();
    }
}

file class NaturalStringComparer : IComparer<string>
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

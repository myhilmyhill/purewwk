using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using System.IO;
using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;

namespace repos.Services;

public class LuceneService : IDisposable
{
    private readonly string _indexPath;
    private readonly Lucene.Net.Store.Directory _directory;
    private readonly Lucene.Net.Analysis.Analyzer _analyzer;
    private readonly IndexWriter _writer;
    private readonly LuceneVersion _version = LuceneVersion.LUCENE_48;
    private readonly string[] _musicExtensions = [".mp3", ".flac", ".wav", ".ogg", ".mp4", ".m4a", ".aac", ".wma"];

    public LuceneService(IConfiguration configuration)
    {
        var workingDir = configuration["WorkingDirectory"];
        var baseDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
        var indexPath = Path.Combine(baseDir, "music_index");
        
        _indexPath = indexPath;
        _directory = Lucene.Net.Store.FSDirectory.Open(indexPath);
        _analyzer = new StandardAnalyzer(_version);
        var config = new IndexWriterConfig(_version, _analyzer);
        _writer = new IndexWriter(_directory, config);
    }    public bool IsIndexValid()
    {
        try
        {
            if (!Directory.Exists(_indexPath))
            {
                return false;
            }

            // Check if index directory has any files
            if (!Directory.GetFiles(_indexPath, "*", SearchOption.AllDirectories).Any())
            {
                return false;
            }

            // Try to open and read the index
            using var reader = DirectoryReader.Open(_directory);
            return reader.NumDocs > 0; // Return true if index has documents
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Index validation failed: {ex.Message}");
            return false;
        }
    }

    public void ClearIndex()
    {
        _writer.DeleteAll();
        _writer.Commit();
        Console.WriteLine("Index cleared");
    }

    public void RemoveFromIndex(string path)
    {
        // パスに基づいてドキュメントを削除
        var query = new TermQuery(new Term("path", path));
        _writer.DeleteDocuments(query);
        
        // 該当パスで始まる子要素も削除（ディレクトリの場合）
        if (Directory.Exists(path))
        {
            var prefixQuery = new PrefixQuery(new Term("path", path + Path.DirectorySeparatorChar));
            _writer.DeleteDocuments(prefixQuery);
        }
        
        _writer.Commit();
        Console.WriteLine($"Removed from index: {path}");
    }

    private bool IsMusicFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return _musicExtensions.Contains(extension);
    }

    private bool DirectoryContainsMusicFiles(string dirPath)
    {
        try
        {
            // Check if directory has any music files directly
            if (Directory.GetFiles(dirPath).Any(file => IsMusicFile(file)))
            {
                return true;
            }

            // Recursively check subdirectories
            return Directory.GetDirectories(dirPath).Any(subDir => DirectoryContainsMusicFiles(subDir));
        }
        catch (Exception)
        {
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
            
            if (Directory.Exists(fullPath))
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
                    Console.WriteLine($"Skipping directory (no music files): {fullPath}");
                }
            }
            else if (File.Exists(fullPath) && IsMusicFile(fullPath))
            {
                // Only index music files
                var parentPath = Path.GetDirectoryName(relativePath);
                var parentId = string.IsNullOrEmpty(parentPath) ? "/" : "/" + parentPath.Replace('\\', '/');
                
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
            else if (File.Exists(fullPath))
            {
                Console.WriteLine($"Skipping non-music file: {fullPath}");
            }
            
            _writer.Commit();
            Console.WriteLine($"Added/Updated in index: {fullPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to add/update path in index: {fullPath}, Error: {ex.Message}");
        }
    }

    private void IndexDirectoryIncremental(string dirPath, string currentId, string musicRootPath)
    {
        var dirInfo = new DirectoryInfo(dirPath);
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
        
        foreach (var file in dirInfo.GetFiles())
        {
            // Only index music files
            if (IsMusicFile(file.FullName))
            {
                var relativePath = Path.GetRelativePath(musicRootPath, file.FullName).Replace('\\', '/');
                var fileId = "/" + relativePath;
                
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

    public void IndexDirectory(string dirPath, string? parentId = null)
    {
        Console.WriteLine($"Indexing directory: {dirPath} with parentId: {parentId}");
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
        var dirInfo = new DirectoryInfo(dirPath);
        foreach (var subDir in dirInfo.GetDirectories())
        {
            // Only index directories that contain music files
            if (DirectoryContainsMusicFiles(subDir.FullName))
            {
                var subDirId = $"{currentId.TrimEnd('/')}/{subDir.Name}";
                Console.WriteLine($"Indexing subdir: {subDir.Name} id: {subDirId}");
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
                Console.WriteLine($"Skipping directory (no music files): {subDir.Name}");
            }
        }
        foreach (var file in dirInfo.GetFiles())
        {
            // Only index music files
            if (IsMusicFile(file.FullName))
            {
                var fileId = $"{currentId.TrimEnd('/')}/{file.Name}";
                Console.WriteLine($"Indexing music file: {file.Name} id: {fileId}");
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
            else
            {
                Console.WriteLine($"Skipping non-music file: {file.Name}");
            }
        }
        _writer.Commit();
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
            Console.WriteLine("Index not found, returning empty result");
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

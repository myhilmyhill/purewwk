using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using System.IO;

namespace repos.Services;

public class LuceneService : IDisposable
{
    private readonly string _indexPath;
    private readonly Lucene.Net.Store.Directory _directory;
    private readonly Lucene.Net.Analysis.Analyzer _analyzer;
    private readonly IndexWriter _writer;
    private readonly LuceneVersion _version = LuceneVersion.LUCENE_48;

    public LuceneService()
    {
        var indexPath = Path.Combine(AppContext.BaseDirectory, "music_index");
        if (Directory.Exists(indexPath))
        {
            try
            {
                Directory.Delete(indexPath, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete index directory: {ex.Message}");
            }
        }
        _indexPath = indexPath;
        _directory = Lucene.Net.Store.FSDirectory.Open(indexPath);
        _analyzer = new StandardAnalyzer(_version);
        var config = new IndexWriterConfig(_version, _analyzer);
        _writer = new IndexWriter(_directory, config);
    }    public void IndexDirectory(string dirPath, string? parentId = null)
    {
        Console.WriteLine($"Indexing directory: {dirPath} with parentId: {parentId}");
        var currentId = parentId ?? "/";
        if (parentId == null)
        {
            // root directory
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
        foreach (var file in dirInfo.GetFiles())
        {
            var fileId = $"{currentId.TrimEnd('/')}/{file.Name}";
            Console.WriteLine($"Indexing file: {file.Name} id: {fileId}");
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
        _writer.Commit();
    }

    public List<Dictionary<string, string>> GetChildren(string parentId)
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
        return result;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _directory?.Dispose();
        _analyzer?.Dispose();
    }
}

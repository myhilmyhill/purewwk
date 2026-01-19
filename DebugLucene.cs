using System;
using System.IO;
using Lucene.Net.Store;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Documents;

namespace DebugLucene
{
    class Program
    {
        static void Main(string[] args)
        {
            var indexPath = "/app/music_index";
            if (!System.IO.Directory.Exists(indexPath))
            {
                Console.WriteLine("Index directory not found at " + indexPath);
                return;
            }

            try
            {
                using var directory = FSDirectory.Open(indexPath);
                using var reader = DirectoryReader.Open(directory);
                var searcher = new IndexSearcher(reader);
                
                Console.WriteLine($"Total Documents: {reader.NumDocs}");

                var query = new MatchAllDocsQuery();
                var hits = searcher.Search(query, 100).ScoreDocs;

                foreach (var hit in hits)
                {
                    var doc = searcher.Doc(hit.Doc);
                    var id = doc.Get("id");
                    var path = doc.Get("path");
                    var isDir = doc.Get("isDir");
                    var parent = doc.Get("parent");
                    Console.WriteLine($"Doc: ID={id}, Parent={parent}, IsDir={isDir}, Path={path}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}

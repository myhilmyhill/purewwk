using System.Collections.Generic;

namespace Purewwk.Plugin.Abstractions;

public interface ILuceneService
{
    void AddOrUpdatePath(string fullPath, string musicRootPath);
    void RemoveFromIndex(string path);
    void IndexDirectory(string dirPath, string? parentId = null);
    MediaItem? GetDocumentById(string id);
    List<MediaItem> GetChildren(string parentId);
    bool IsIndexValid();
    void ClearIndex();
}

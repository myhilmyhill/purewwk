using Purewwk.Plugin;
using System.Collections.Generic;

namespace Purewwk.Services;

public interface ILuceneService : IIndexUpdater
{
    void IndexDirectory(string dirPath, string? parentId = null);
    MediaItem? GetDocumentById(string id);
    List<MediaItem> GetChildren(string parentId);
    bool IsIndexValid();
    void ClearIndex();
}

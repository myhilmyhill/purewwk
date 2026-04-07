namespace Purewwk.Plugin;

public interface IIndexUpdater
{
    void AddOrUpdatePath(string fullPath, string musicRootPath);
    void RemoveFromIndex(string path);
}

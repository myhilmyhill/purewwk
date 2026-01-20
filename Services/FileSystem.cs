using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace repos.Services;

public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IEnumerable<string> GetFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    IEnumerable<string> GetDirectories(string path);
    IFileSystemDirectoryInfo GetDirectoryInfo(string path);
    string GetExtension(string path);
    string GetFileName(string path);
    string GetDirectoryName(string path);
    string GetRelativePath(string relativeTo, string path);
    byte[] ReadAllBytes(string path);
}

public interface IFileSystemDirectoryInfo
{
    string Name { get; }
    string FullName { get; }
    IEnumerable<IFileSystemDirectoryInfo> GetDirectories();
    IEnumerable<IFileSystemFileInfo> GetFiles();
    IEnumerable<IFileSystemFileInfo> GetFiles(string searchPattern);
}

public interface IFileSystemFileInfo
{
    string Name { get; }
    string FullName { get; }
    string Extension { get; }
    bool Exists { get; }
}

public class RealFileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public IEnumerable<string> GetFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => Directory.GetFiles(path, searchPattern, searchOption);
    public IEnumerable<string> GetDirectories(string path) => Directory.GetDirectories(path);
    public IFileSystemDirectoryInfo GetDirectoryInfo(string path) => new RealFileSystemDirectoryInfo(new DirectoryInfo(path));
    public string GetExtension(string path) => Path.GetExtension(path);
    public string GetFileName(string path) => Path.GetFileName(path);
    public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? "";
    public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
}

public class RealFileSystemDirectoryInfo : IFileSystemDirectoryInfo
{
    private readonly DirectoryInfo _info;
    public RealFileSystemDirectoryInfo(DirectoryInfo info) => _info = info;

    public string Name => _info.Name;
    public string FullName => _info.FullName;

    public IEnumerable<IFileSystemDirectoryInfo> GetDirectories()
    {
        return _info.GetDirectories().Select(d => new RealFileSystemDirectoryInfo(d));
    }

    public IEnumerable<IFileSystemFileInfo> GetFiles()
    {
        return _info.GetFiles().Select(f => new RealFileSystemFileInfo(f));
    }

    public IEnumerable<IFileSystemFileInfo> GetFiles(string searchPattern)
    {
        return _info.GetFiles(searchPattern).Select(f => new RealFileSystemFileInfo(f));
    }
}

public class RealFileSystemFileInfo : IFileSystemFileInfo
{
    private readonly FileInfo _info;
    public RealFileSystemFileInfo(FileInfo info) => _info = info;

    public string Name => _info.Name;
    public string FullName => _info.FullName;
    public string Extension => _info.Extension;
    public bool Exists => _info.Exists;
}

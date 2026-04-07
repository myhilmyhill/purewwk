using System.IO;
using System.Collections.Generic;

namespace Purewwk.Plugin.Abstractions;

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

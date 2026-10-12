using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

public interface IFolderBrowser
{
    IReadOnlyList<FolderNode> GetRoots();

    IReadOnlyList<FolderNode> GetSubfolders(string path);
}

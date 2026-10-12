namespace Amberglass.Core.Models;

public sealed record ScanProgress(int FilesFound, int FoldersVisited, string? CurrentFolder);

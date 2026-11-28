namespace DeepFind.Core;

public sealed record Settings
{
    public string[] Folders { get; init; } = [];
    public string[] Exclusions { get; init; } = [];
    public int MaxMegabytes { get; init; } = 100;
    public bool IncludeOtherFiles { get; init; }
    public int LocalHours { get; init; } = 6;
    public int NetworkHours { get; init; } = 24;
    public string? IndexLocation { get; init; }
    public string Theme { get; init; } = "System";
    public bool Paused { get; init; }
    public int PageSize { get; init; } = 250;
    public bool ShowPath { get; init; } = true;
    public bool ShowLocation { get; init; } = true;
    public bool ShowExcerpt { get; init; } = true;
    public bool ShowModified { get; init; } = true;
}

public sealed record TextPart(string Text, string? Location = null);
public sealed record ExtractedDocument(IReadOnlyList<TextPart> Parts, string? Notice = null);
public sealed record FileVersion(string Path, long Size, long ModifiedTicks)
{
    public string Id => Path.GetFullPathKey();
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
}

public sealed record IndexedDocument(FileVersion File, IReadOnlyList<TextPart> Parts);
public sealed record FileState(FileVersion File, string? Issue, bool Retry, bool Unavailable);
public sealed record SearchHit(FileVersion File, string Snippet, string? Location,
    IReadOnlyList<string> Highlights);
public sealed record SearchResults(int Total, IReadOnlyList<SearchHit> Hits, int Page = 1);
public sealed record IndexProgress(int Processed, int Discovered, int Errors, string Message, int Updated = 0);
public enum FileFilter { All, Documents, Pdf, Excel }
public enum SortOrder { Relevance, Modified, Name, Size, Path, ModifiedOldest, NameDescending, SizeAscending, PathDescending }
public sealed record SearchRequest(string Query, FileFilter Filter, SortOrder Sort, int Limit = 250, int Page = 1);

public interface IContentExtractor
{
    bool CanExtract(string extension);
    ExtractedDocument Extract(string path, CancellationToken cancellationToken);
}

public interface ISearchIndex : IDisposable
{
    void Upsert(IndexedDocument document);
    void Delete(string id);
    void Clear();
    void Commit();
    void Rollback();
    IReadOnlyDictionary<string, FileVersion> GetVersions();
    SearchResults Search(SearchRequest request, CancellationToken cancellationToken);
}

public static class Paths
{
    public static string GetFullPathKey(this string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    public static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}

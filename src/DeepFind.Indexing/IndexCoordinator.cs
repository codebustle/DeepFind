using System.Diagnostics;
using System.IO.Enumeration;
using DeepFind.Core;
using DeepFind.Extraction;
using DeepFind.Infrastructure;

namespace DeepFind.Indexing;

public sealed class IndexCoordinator(ISearchIndex index, TrackingStore tracking)
{
    private readonly IContentExtractor extractor = new ContentExtractor();
    private readonly SemaphoreSlim gate = new(1, 1);
    private const int BatchSize = 64;

    public Task ReconcileAsync(Settings settings, IProgress<IndexProgress>? progress, CancellationToken token,
        IReadOnlyCollection<string>? activeRoots = null, string? retryPath = null) =>
        RunAsync(() => Reconcile(settings, progress, token, activeRoots, retryPath), token);

    public Task ClearAsync(CancellationToken token) => RunAsync(() =>
    {
        tracking.RequestScan();
        index.Clear();
        index.Commit();
        tracking.Save([], tracking.Load().Keys);
    }, token);

    public Task PruneAsync(Settings settings, CancellationToken token) => RunAsync(() =>
    {
        var previous = tracking.Load();
        var existing = index.GetVersions();
        var removed = previous.Values.Select(state => state.File).Concat(existing.Values)
            .Where(file => !settings.Folders.Any(root => Paths.IsWithin(file.Path, root)) ||
                IsExcluded(file.Path, settings) || (!settings.IncludeOtherFiles && !extractor.CanExtract(file.Extension)))
            .Select(file => file.Id).Distinct().ToArray();
        if (removed.Length == 0)
            return;
        tracking.RequestScan();
        foreach (var id in removed)
            index.Delete(id);
        index.Commit();
        tracking.Save([], removed);
    }, token);

    private async Task RunAsync(Action action, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(action, token).ConfigureAwait(false);
        }
        catch
        {
            index.Rollback();
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private void Reconcile(Settings settings, IProgress<IndexProgress>? progress, CancellationToken token,
        IReadOnlyCollection<string>? activeRoots, string? retryPath)
    {
        var (generation, hadPendingWork) = tracking.BeginScan();
        var previous = tracking.Load();
        var existing = index.GetVersions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var updates = new List<FileState>();
        var removals = new List<string>();
        var roots = hadPendingWork ? settings.Folders : activeRoots ?? settings.Folders;
        var progressTimer = Stopwatch.StartNew();
        var discovered = 0;
        var errors = 0;
        var updated = 0;

        foreach (var path in Discover(roots, settings, blocked, token))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(path.GetFullPathKey()))
                continue;
            discovered++;
            FileState state;
            try
            {
                var file = new FileInfo(path);
                var version = new FileVersion(path, file.Length, file.LastWriteTimeUtc.Ticks);
                var unchanged = existing.TryGetValue(version.Id, out var published) && published == version;
                previous.TryGetValue(version.Id, out var old);
                if (unchanged && old is not null && old.File == version && !old.Retry &&
                    !string.Equals(retryPath, path, StringComparison.OrdinalIgnoreCase) &&
                    (old.Issue == "File exceeds the configured size limit.") ==
                        (version.Size > settings.MaxMegabytes * 1024L * 1024))
                {
                    if (old.Unavailable)
                        updates.Add(old with { Unavailable = false });
                    if (updates.Count == BatchSize) Flush();
                    ReportProgress();
                    continue;
                }
                updated++;
                state = Publish(version, settings, token, unchanged);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (previous.TryGetValue(path.GetFullPathKey(), out var old))
                    state = old with { Issue = Describe(error), Retry = true, Unavailable = true };
                else
                    state = new(new(path, 0, 0), Describe(error), true, true);
            }

            updates.Add(state);
            if (state.Issue is not null)
                errors++;
            if (updates.Count == BatchSize)
                Flush();
            ReportProgress();
        }

        foreach (var id in existing.Keys.Union(previous.Keys, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var file = previous.TryGetValue(id, out var state) ? state.File : existing[id];
            var retained = settings.Folders.Any(folder => Paths.IsWithin(file.Path, folder)) &&
                !IsExcluded(file.Path, settings) && (settings.IncludeOtherFiles || extractor.CanExtract(file.Extension));
            if (!retained || (!seen.Contains(id) && roots.Any(root => Paths.IsWithin(file.Path, root))))
            {
                if (retained && UnderBlocked(file.Path, blocked))
                    updates.Add(new(file, "Source is unavailable.", true, true));
                else
                {
                    index.Delete(id);
                    removals.Add(id);
                }
            }

            if (updates.Count + removals.Count >= BatchSize)
                Flush();
        }

        Flush();
        tracking.CompleteScan(generation);
        progress?.Report(new(discovered, discovered, errors + blocked.Count, "Index is up to date", updated));
        return;

        void ReportProgress()
        {
            if (progress is null || progressTimer.ElapsedMilliseconds < 120)
                return;
            progress.Report(new(discovered, discovered, errors, updated == 0 ?
                "Checking indexed folders" : "Indexing files", updated));
            progressTimer.Restart();
        }

        void Flush()
        {
            if (updates.Count + removals.Count == 0)
                return;
            // Published Lucene versions decide what is current. Interrupted metadata writes are reconciled on restart.
            index.Commit();
            tracking.Save(updates, removals);
            updates.Clear();
            removals.Clear();
        }
    }

    private FileState Publish(FileVersion version, Settings settings, CancellationToken token, bool unchanged)
    {
        if (version.Size > settings.MaxMegabytes * 1024L * 1024)
        {
            index.Upsert(new(version, []));
            return new(version, "File exceeds the configured size limit.", false, false);
        }

        if (!extractor.CanExtract(version.Extension))
        {
            index.Upsert(new(version, []));
            return new(version, null, false, false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ExtractedDocument extracted;
        try
        {
            extracted = extractor.Extract(version.Path, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            var current = new FileInfo(version.Path);
            if (current.Length != version.Size || current.LastWriteTimeUtc.Ticks != version.ModifiedTicks)
                return new(version, "File changed during extraction. It will be retried.", true, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            token.ThrowIfCancellationRequested();
            var unavailable = error is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException;
            if (!unavailable || !unchanged)
                index.Upsert(new(version, []));
            return new(version, Describe(error), true, unavailable);
        }

        index.Upsert(new(version, extracted.Parts));
        return new(version, extracted.Notice, false, false);
    }

    private IEnumerable<string> Discover(IEnumerable<string> roots, Settings settings, HashSet<string> blocked,
        CancellationToken token)
    {
        var work = new Stack<(string Path, bool Root)>(roots.Select(root => (root, true)));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (work.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(item.Path.GetFullPathKey()) || IsExcluded(item.Path, settings))
                continue;
            string[] entries;
            try
            {
                var attributes = File.GetAttributes(item.Path);
                if (!item.Root && attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                entries = Directory.GetFileSystemEntries(item.Path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A missing configured root may be a disconnected volume. Preserve its previously indexed files.
                blocked.Add(item.Path.GetFullPathKey());
                continue;
            }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (IsExcluded(entry, settings))
                    continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    blocked.Add(entry.GetFullPathKey());
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                    work.Push((entry, false));
                else if (settings.IncludeOtherFiles || extractor.CanExtract(Path.GetExtension(entry).TrimStart('.').ToLowerInvariant()))
                    yield return entry;
            }
        }
    }

    private static bool UnderBlocked(string path, HashSet<string> blocked)
    {
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if (blocked.Contains(parent.GetFullPathKey()))
                return true;
        return false;
    }

    private static bool IsExcluded(string path, Settings settings) => settings.Exclusions.Any(pattern =>
        FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path), ignoreCase: true) ||
        FileSystemName.MatchesSimpleExpression(pattern, path, ignoreCase: true) ||
        AncestorMatches(path, pattern));

    private static bool AncestorMatches(string path, string pattern)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(parent), ignoreCase: true) ||
                FileSystemName.MatchesSimpleExpression(pattern, parent, ignoreCase: true))
                return true;
        return false;
    }

    private static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "Extraction exceeded the time limit.",
        UnauthorizedAccessException => "Access denied.",
        FileNotFoundException => "File was unavailable during indexing.",
        IOException when error is not InvalidDataException => "The file could not be read.",
        InvalidDataException => error.Message,
        _ => "Document text could not be extracted."
    };
}

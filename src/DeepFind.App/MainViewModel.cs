using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using DeepFind.Core;
using DeepFind.Indexing;
using DeepFind.Infrastructure;
using DeepFind.Search;
using Serilog;

namespace DeepFind.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsStore store;
    private readonly LuceneSearchIndex index;
    private readonly TrackingStore tracking;
    private readonly IndexCoordinator coordinator;
    private readonly DispatcherTimer timer;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Dictionary<string, DateTime> lastScan = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? searchCancellation;
    private Task? scanTask;
    private Task? searchTask;
    private IReadOnlyDictionary<string, FileState> states = new Dictionary<string, FileState>();
    private long changedAt;
    private DateTime retryAfter;
    private bool managing;
    private string query = "";
    private FileFilter filter;
    private SortOrder sort;
    private string searchMessage = "";
    private string indexMessage = "";
    private IReadOnlyList<ResultRow> results = [];
    private int total;

    public MainViewModel(SettingsStore store, Settings settings, string indexDirectory)
    {
        this.store = store;
        Settings = settings;
        IndexDirectory = indexDirectory;
        tracking = new(indexDirectory);
        index = new(Path.Combine(indexDirectory, "Search"));
        coordinator = new(index, tracking);
        states = tracking.Load();
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += Tick;
    }

    public Settings Settings { get; private set; }
    public string IndexDirectory { get; }
    public string StartupLocation => store.IndexDirectory(Settings);
    public IReadOnlyList<FileState> Issues => states.Values.Where(state => state.Issue is not null)
        .OrderBy(state => state.File.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    public bool Busy => scanTask is { IsCompleted: false };
    public string Query { get => query; set { if (Set(ref query, value)) QueueSearch(delay: true); } }
    public FileFilter Filter { get => filter; set { if (Set(ref filter, value)) QueueSearch(delay: false); } }
    public SortOrder Sort { get => sort; set { if (Set(ref sort, value)) QueueSearch(delay: false); } }
    public IReadOnlyList<ResultRow> Results { get => results; private set => Set(ref results, value); }
    public string SearchMessage { get => searchMessage; private set => Set(ref searchMessage, value); }
    public string IndexMessage { get => indexMessage; private set => Set(ref indexMessage, value); }
    public string ResultCount => total == 0 ? "" : Results.Count < total
        ? $"{Results.Count:N0} of {total:N0} results"
        : total == 1 ? "1 result" : $"{total:N0} results";
    public bool HasFolders => Settings.Folders.Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task StartAsync()
    {
        IndexMessage = Settings.Paused ? "Index paused" : HasFolders ? "Checking indexed folders" : "";
        RefreshWatchers();
        timer.Start();
        QueueSearch(delay: false);
        await RunIndexAsync(pruneOnly: Settings.Paused);
    }

    public void QueueSearch(bool delay)
    {
        if (closing.IsCancellationRequested)
            return;
        searchCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        searchCancellation = cancellation;
        var previous = searchTask;
        searchTask = SearchAsync(new(Query, Filter, Sort), delay, cancellation, previous);
    }

    public void CancelSearch() => searchCancellation?.Cancel();
    public string ReadPreview(SearchHit hit) => index.ReadPreview(hit);

    private async Task SearchAsync(SearchRequest request, bool delay, CancellationTokenSource owner, Task? previous)
    {
        // Start after QueueSearch assigns the current task, even when there is no delay.
        await Task.Yield();
        using (owner)
        try
        {
            if (previous is not null)
                await previous;
            var token = owner.Token;
            if (delay)
                await Task.Delay(200, token);
            var found = await Task.Run(() => index.Search(request, token), token);
            token.ThrowIfCancellationRequested();
            Results = found.Hits.Select(hit => new ResultRow(hit,
                states.TryGetValue(hit.File.Id, out var state) && state.Unavailable)).ToArray();
            total = found.Total;
            SearchMessage = !HasFolders ? "Add a folder to start searching your files."
                : total == 0 ? Busy ? "Indexing is in progress." : request.Query.Length > 0
                    ? "No matching files." : "No indexed files to show."
                : "";
            Changed(nameof(ResultCount));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Results = [];
            total = 0;
            Changed(nameof(ResultCount));
            if (error is not FormatException)
                Log.Warning("Search failed with {ErrorType}", error.GetType().Name);
            SearchMessage = error is FormatException ? error.Message : "Search could not read the local index.";
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, owner))
                searchCancellation = null;
        }
    }

    public async Task ApplySettingsAsync(Settings settings)
    {
        managing = true;
        try
        {
            await StopScanAsync();
            store.Save(settings);
            Settings = settings;
            lastScan.Clear();
            RefreshWatchers();
            Changed(nameof(Settings));
            Changed(nameof(HasFolders));
            await RunIndexAsync(pruneOnly: true, surfaceErrors: true);
        }
        finally { managing = false; }
        if (!Settings.Paused)
            _ = RunIndexAsync();
    }

    public async Task SetPausedAsync(bool paused)
    {
        managing = true;
        try
        {
            var settings = Settings with { Paused = paused };
            store.Save(settings);
            Settings = settings;
            Changed(nameof(Settings));
            if (paused)
            {
                await StopScanAsync();
                IndexMessage = "Index paused";
            }
        }
        finally { managing = false; }
        if (!paused)
            _ = RunIndexAsync();
    }

    public async Task ResetIndexAsync(bool rebuild)
    {
        managing = true;
        try
        {
            await StopScanAsync();
            await coordinator.ClearAsync(closing.Token);
            states = tracking.Load();
            Changed(nameof(Issues));
            QueueSearch(delay: false);
            if (!rebuild)
                await SetPausedAsync(true);
        }
        finally { managing = false; }
        if (rebuild)
        {
            if (Settings.Paused)
                await SetPausedAsync(false);
            else
                _ = RunIndexAsync();
        }
    }

    public Task UpdateAsync(string? retryPath = null) => RunIndexAsync(retryPath: retryPath);

    private Task RunIndexAsync(bool pruneOnly = false, IReadOnlyCollection<string>? roots = null, string? retryPath = null,
        bool surfaceErrors = false)
    {
        if (closing.IsCancellationRequested || Busy || (!pruneOnly && Settings.Paused))
            return Task.CompletedTask;
        scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        scanTask = ScanAsync(pruneOnly, roots, retryPath, scanCancellation, surfaceErrors);
        return scanTask;
    }

    private async Task ScanAsync(bool pruneOnly, IReadOnlyCollection<string>? roots, string? retryPath,
        CancellationTokenSource owner, bool surfaceErrors)
    {
        await Task.Yield();
        using (owner)
        try
        {
            Changed(nameof(Busy));
            var token = owner.Token;
            // Prevent recursive indexing when a selected folder contains DeepFind's application data.
            var effective = Settings with
            {
                Exclusions = Settings.Exclusions.Concat(new[] { store.DataDirectory, IndexDirectory, StartupLocation })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
            if (pruneOnly)
                await coordinator.PruneAsync(effective, token);
            else
            {
                IndexMessage = !HasFolders ? "" : await Task.Run(tracking.HasPendingScan, token)
                    ? "Resuming pending index update" : "Checking indexed folders";
                if (roots is null)
                    Interlocked.Exchange(ref changedAt, 0);
                var progress = new Progress<IndexProgress>(value =>
                {
                    if (ReferenceEquals(scanCancellation, owner) && value.Message == "Indexing files")
                        IndexMessage = $"Indexing · {value.Processed:N0} checked";
                });
                await coordinator.ReconcileAsync(effective, progress, token, roots, retryPath);
                foreach (var root in roots ?? Settings.Folders)
                    lastScan[root] = DateTime.UtcNow;
                RefreshWatchers();
            }

            states = await Task.Run(tracking.Load, token);
            Changed(nameof(Issues));
            var issues = states.Values.Count(state => state.Issue is not null);
            IndexMessage = Settings.Paused ? "Index paused" : !HasFolders ? "" : issues == 0
                ? "Index up to date" : $"Index updated · {issues:N0} file notices";
        }
        catch (OperationCanceledException) { IndexMessage = Settings.Paused ? "Index paused" : IndexMessage; }
        catch (Exception error)
        {
            Log.Error("Index update failed with {ErrorType}", error.GetType().Name);
            retryAfter = DateTime.UtcNow.AddSeconds(30);
            IndexMessage = "Index update failed. Retry from Index settings.";
            if (surfaceErrors)
                throw;
        }
        finally
        {
            if (ReferenceEquals(scanCancellation, owner))
                scanCancellation = null;
            scanTask = null;
            Changed(nameof(Busy));
            QueueSearch(delay: false);
        }
    }

    private async Task StopScanAsync()
    {
        scanCancellation?.Cancel();
        if (scanTask is { } task)
            await task;
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (Busy || managing || Settings.Paused || !HasFolders)
            return;
        var now = DateTime.UtcNow;
        if (now < retryAfter)
            return;
        var change = Interlocked.Read(ref changedAt);
        if (change != 0 && now.Ticks - change > TimeSpan.FromSeconds(2).Ticks)
        {
            _ = RunIndexAsync();
            return;
        }
        var due = Settings.Folders.Where(root => !lastScan.TryGetValue(root, out var checkedAt) ||
            now - checkedAt >= TimeSpan.FromHours(IsNetwork(root) ? Settings.NetworkHours : Settings.LocalHours)).ToArray();
        if (due.Length > 0)
            _ = RunIndexAsync(roots: due);
    }

    private void RefreshWatchers()
    {
        foreach (var watcher in watchers)
            watcher.Dispose();
        watchers.Clear();
        foreach (var root in Settings.Folders)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += (_, e) => MarkChange(e.FullPath);
                watcher.Created += (_, e) => MarkChange(e.FullPath);
                watcher.Deleted += (_, e) => MarkChange(e.FullPath);
                watcher.Renamed += (_, e) => { MarkChange(e.OldFullPath); MarkChange(e.FullPath); };
                watcher.Error += (_, _) => RequestWatcherScan();
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            {
                watcher?.Dispose(); // Periodic reconciliation retries unavailable watchers.
            }
        }
    }

    private void MarkChange(string path)
    {
        if (!Paths.IsWithin(path, store.DataDirectory) && !Paths.IsWithin(path, IndexDirectory))
            RequestWatcherScan();
    }

    private void RequestWatcherScan()
    {
        // Coalesce each watcher burst into one durable reconciliation request.
        if (Interlocked.Exchange(ref changedAt, DateTime.UtcNow.Ticks) != 0)
            return;
        try { tracking.RequestScan(); }
        catch (Exception error)
        {
            Log.Warning("Could not journal a folder change with {ErrorType}", error.GetType().Name);
        }
    }

    private static bool IsNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network; }
        catch (IOException) { return false; }
    }

    public async Task CloseAsync()
    {
        closing.Cancel();
        timer.Stop();
        foreach (var watcher in watchers)
            watcher.Dispose();
        await StopScanAsync();
        if (searchTask is { } current)
            await current;
        index.Dispose();
        closing.Dispose();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Changed(name);
        return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record ResultRow(SearchHit Hit, bool Unavailable)
{
    public string Name => Path.GetFileName(Hit.File.Path);
    public string PathText => Unavailable ? $"{Hit.File.Path} · Unavailable" : Hit.File.Path;
    public string Location => Hit.Location is null ? "" : $"{Hit.Location}  ·  ";
}

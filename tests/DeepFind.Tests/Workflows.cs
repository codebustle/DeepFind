using System.Text;
using DeepFind.Core;
using DeepFind.Indexing;
using DeepFind.Infrastructure;
using DeepFind.Search;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DeepFind.Tests;

public sealed class Workflows : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("deepfind-test-").FullName;
    private string Source => Path.Combine(workspace, "sources");
    private string Data => Path.Combine(workspace, "index");
    private Settings Options => new() { Folders = [Source] };
    private string FilePath(string name) => Path.Combine(Source, name);
    private LuceneSearchIndex OpenIndex() => new(Path.Combine(Data, "Search"));

    [Fact]
    public async Task EightFormatsAreSearchableAfterRestartWithLocationReferences()
    {
        Directory.CreateDirectory(Source);
        WriteFixtures();

        using (var index = OpenIndex())
            await new IndexCoordinator(index, new(Data)).ReconcileAsync(Options, null, TestContext);

        using var reopened = OpenIndex();
        foreach (var (term, file, location) in new (string, string, string?)[]
        {
            ("harbour", "notes.txt", null), ("winch", "maintenance.md", null),
            ("draught", "voyage.csv", null), ("anchors", "procedure.html", null),
            ("towing", "manual.pdf", "Page 2"), ("waterline", "survey.docx", null),
            ("ballast", "metrics.xlsx", "Inspection!B2"), ("docking", "slides.pptx", "Slide 1")
        })
        {
            var hit = Assert.Single(Search(reopened, term).Hits);
            Assert.Equal(FilePath(file), hit.File.Path);
            Assert.Equal(location, hit.Location);
            Assert.Contains(term, hit.Highlights);
            Assert.Contains(term, hit.Snippet, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0, Search(reopened, "scriptneedle").Total);
        Assert.Equal(1, Search(reopened, "filename:notes").Total);
        Assert.Equal(1, Search(reopened, "filename:notes.txt").Total);
        Assert.Equal(1, Search(reopened, "ext:pdf \"emergency towing\"").Total);
        Assert.Equal(2, Search(reopened, "waterline OR ballast AND valve").Total);
        Assert.Equal(1, Search(reopened, "harbour AND arrival").Total);
        Assert.Equal(0, Search(reopened, "ballast", FileFilter.Pdf).Total);
        Assert.Equal(2, reopened.Search(new("", FileFilter.Excel, SortOrder.Name), TestContext).Total);
        Assert.Throws<FormatException>(() => Search(reopened, "ext:pdf OR"));
        Assert.Throws<FormatException>(() => Search(reopened, "notes*"));
        Assert.Throws<FormatException>(() => Search(reopened, "\"unterminated"));
        // Reuse the exact validated document fixtures for native Windows acceptance tests.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp)
        {
            var target = Path.Combine(runnerTemp, "deepfind-ui-formats");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(Source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    [Fact]
    public async Task TruncatedSearchManifestIsReportedAsRecoverableCorruption()
    {
        Directory.CreateDirectory(Source);
        var original = FilePath("original.txt");
        File.WriteAllText(original, "recoverableoriginal");
        using (var index = OpenIndex())
            await new IndexCoordinator(index, new(Data)).ReconcileAsync(Options, null, TestContext);

        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(Data, "Search"), "segments_*"));
        File.WriteAllBytes(manifest, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        var failure = Assert.Throws<SearchIndexTruncatedException>(() =>
        {
            using var _ = OpenIndex();
        });
        Assert.IsType<EndOfStreamException>(failure.InnerException);
        Assert.Equal("recoverableoriginal", File.ReadAllText(original));
    }

    [Fact]
    public async Task ReconciliationHandlesChangesRenamesOfflineRootsAndConfirmedDeletes()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(FilePath("before.txt"), "originalneedle");
        using var index = OpenIndex();
        var tracking = new TrackingStore(Data);
        var runner = new IndexCoordinator(index, tracking);
        await runner.ReconcileAsync(Options, null, TestContext);
        Assert.Equal(1, Search(index, "originalneedle").Total);

        File.Move(FilePath("before.txt"), FilePath("after.txt"));
        File.WriteAllText(FilePath("after.txt"), "replacementneedle");
        await runner.ReconcileAsync(Options, null, TestContext);
        Assert.Equal(0, Search(index, "originalneedle OR filename:before").Total);
        Assert.Equal(FilePath("after.txt"), Assert.Single(Search(index, "replacementneedle").Hits).File.Path);

        var offline = Path.Combine(workspace, "offline");
        Directory.Move(Source, offline);
        await runner.ReconcileAsync(Options, null, TestContext);
        Assert.True(Assert.Single(tracking.Load()).Value.Unavailable);
        Assert.Equal(1, Search(index, "replacementneedle").Total);

        Directory.Move(offline, Source);
        await runner.ReconcileAsync(Options, null, TestContext);
        Assert.False(Assert.Single(tracking.Load()).Value.Unavailable);
        File.Delete(FilePath("after.txt"));
        await runner.ReconcileAsync(Options, null, TestContext);
        Assert.Empty(tracking.Load());
        Assert.Equal(0, Search(index, "").Total);
    }

    [Fact]
    public async Task BadAndLargeFilesDoNotStopIndexingAndSettingsRemoveIndexedContent()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(FilePath("corrupt.pdf"), "not a PDF");
        File.WriteAllText(FilePath("small.txt"), "needle");
        File.WriteAllText(FilePath("legacy.txt"), "café", Encoding.Latin1);
        using (var file = File.Create(FilePath("large.txt")))
        {
            file.Write(Encoding.UTF8.GetBytes("largefileneedle "));
            file.SetLength(2 * 1024 * 1024);
        }

        using var index = OpenIndex();
        var tracking = new TrackingStore(Data);
        var runner = new IndexCoordinator(index, tracking);
        var limited = Options with { MaxMegabytes = 1 };
        await runner.ReconcileAsync(limited, null, TestContext);
        Assert.Equal(4, Search(index, "").Total);
        Assert.Equal(1, Search(index, "needle").Total);
        Assert.Equal(1, Search(index, "café").Total);
        Assert.Equal(0, Search(index, "largefileneedle").Total);
        Assert.Equal(2, tracking.Load().Values.Count(state => state.Issue is not null));

        await runner.ReconcileAsync(Options, null, TestContext);
        Assert.Equal(1, Search(index, "largefileneedle").Total);
        await runner.ReconcileAsync(limited, null, TestContext);
        Assert.Equal(0, Search(index, "largefileneedle").Total);
        await runner.PruneAsync(Options with { Exclusions = ["small.*"] }, TestContext);
        Assert.Equal(0, Search(index, "needle").Total);
        await runner.PruneAsync(Options with { Folders = [] }, TestContext);
        Assert.Equal(0, Search(index, "").Total);
        Assert.Empty(tracking.Load());
        Assert.True(File.Exists(FilePath("small.txt")));
    }

    [Fact]
    public async Task ACommitSurvivesCancellationAndMissingTrackingRowsAreReconciled()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(FilePath("kept.txt"), "checkpointneedle");
        using (var index = OpenIndex())
            await new IndexCoordinator(index, new(Data)).ReconcileAsync(Options, null, TestContext);

        // Simulate interruption after the Lucene commit but before SQLite publishes its metadata.
        var tracking = new TrackingStore(Data);
        tracking.Save([], tracking.Load().Keys);
        using var reopened = OpenIndex();
        var runner = new IndexCoordinator(reopened, tracking);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ReconcileAsync(
            Options, null, new CancellationToken(canceled: true)));
        Assert.Equal(1, Search(reopened, "checkpointneedle").Total);
        await runner.ReconcileAsync(Options, null, TestContext);
        Assert.Single(tracking.Load());
        Assert.Equal(1, Search(reopened, "checkpointneedle").Total);
        await runner.ClearAsync(TestContext);
        Assert.Equal(0, Search(reopened, "").Total);
    }

    [Fact]
    public void NewReconciliationRequestsSurviveCompletionOfAnOlderScan()
    {
        var tracking = new TrackingStore(Data);
        Assert.False(tracking.HasPendingScan());
        var active = tracking.RequestScan();
        var waiting = tracking.RequestScan();
        tracking.CompleteScan(active);
        var reopened = new TrackingStore(Data);
        Assert.True(reopened.HasPendingScan());
        reopened.CompleteScan(waiting);
        Assert.False(tracking.HasPendingScan());
    }

    [Fact]
    public async Task TenThousandFilesCanBeAccountedFor()
    {
        Directory.CreateDirectory(Source);
        WriteFixtures();
        for (var folder = 0; folder < 100; folder++)
        {
            var subfolder = Directory.CreateDirectory(FilePath($"batch-{folder:D2}")).FullName;
            for (var file = 0; file < 100; file++)
                File.WriteAllText(Path.Combine(subfolder, $"record-{file:D2}.txt"), $"inspection batch {folder} document {file}");
        }

        using var index = OpenIndex();
        var tracking = new TrackingStore(Data);
        await new IndexCoordinator(index, tracking).ReconcileAsync(Options, null, TestContext);
        Assert.Equal(10_008, tracking.Load().Count);
        Assert.Equal(10_000, Search(index, "inspection AND filename:record").Total);
        Assert.Equal(250, Search(index, "inspection AND filename:record").Hits.Count);
        var last = index.Search(new("inspection AND filename:record", FileFilter.All, SortOrder.Name, 250, 40), TestContext);
        Assert.Equal(10_000, last.Total);
        Assert.Equal(40, last.Page);
        Assert.Equal(250, last.Hits.Count);
        var first = index.Search(new("inspection AND filename:record", FileFilter.All, SortOrder.Name, 250, 1), TestContext);
        Assert.Empty(first.Hits.Select(hit => hit.File.Id).Intersect(last.Hits.Select(hit => hit.File.Id)));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(1000)]
    public void PagesCoverEveryMatchWithoutDuplicatesAndClampAfterShrink(int pageSize)
    {
        using var index = OpenIndex();
        for (var n = 0; n < 1001; n++)
            index.Upsert(new(new(FilePath($"record-{n:D4}.txt"), 100, 1000), [new("paginationneedle")]));
        index.Commit();

        foreach (var sort in Enum.GetValues<SortOrder>())
        {
            var actual = new List<string>();
            var count = (1000 / pageSize) + 1;
            for (var page = 1; page <= count; page++)
            {
                var found = index.Search(new("paginationneedle", FileFilter.All, sort, pageSize, page), TestContext);
                Assert.Equal(1001, found.Total);
                Assert.Equal(page, found.Page);
                Assert.Equal(Math.Min(pageSize, 1001 - (page - 1) * pageSize), found.Hits.Count);
                actual.AddRange(found.Hits.Select(hit => hit.File.Id));
            }
            Assert.Equal(1001, actual.Distinct().Count());
            if (sort == SortOrder.Name)
                Assert.Equal(actual.OrderBy(id => id, StringComparer.Ordinal), actual);
            if (sort == SortOrder.NameDescending)
                Assert.Equal(actual.OrderByDescending(id => id, StringComparer.Ordinal), actual);
        }

        index.Clear();
        index.Upsert(new(new(FilePath("last.txt"), 1, 1000), [new("paginationneedle")]));
        index.Commit();
        var clamped = index.Search(new("paginationneedle", FileFilter.All, SortOrder.Name, pageSize, 100), TestContext);
        Assert.Equal(1, clamped.Page);
        Assert.Single(clamped.Hits);
    }

    [Fact]
    public void ModifiedAndNameSortUseStoredValuesAndStableTies()
    {
        using var index = OpenIndex();
        index.Upsert(new(new(FilePath("z.txt"), 1, 100), [new("needle")]));
        index.Upsert(new(new(FilePath("a.txt"), 1, 300), [new("needle")]));
        index.Upsert(new(new(FilePath("m.txt"), 1, 300), [new("needle")]));
        index.Commit();
        string[] Names(SortOrder order) => index.Search(new("needle", FileFilter.All, order), TestContext)
            .Hits.Select(hit => Path.GetFileName(hit.File.Path)).ToArray();
        Assert.Equal(new[] { "a.txt", "m.txt", "z.txt" }, Names(SortOrder.Modified));
        Assert.Equal(new[] { "z.txt", "a.txt", "m.txt" }, Names(SortOrder.ModifiedOldest));
        Assert.Equal(new[] { "a.txt", "m.txt", "z.txt" }, Names(SortOrder.Name));
        Assert.Equal(new[] { "z.txt", "m.txt", "a.txt" }, Names(SortOrder.NameDescending));
    }

    [Fact]
    public async Task CleanRestartChecksUnchangedFilesWithoutPublishingThem()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(FilePath("unchanged.txt"), "restartneedle");
        var tracking = new TrackingStore(Data);
        using (var index = OpenIndex())
            await new IndexCoordinator(index, tracking).ReconcileAsync(Options, null, TestContext);
        Assert.False(tracking.HasPendingScan());

        using var reopened = OpenIndex();
        var events = new List<IndexProgress>();
        await new IndexCoordinator(reopened, tracking).ReconcileAsync(Options, new InlineProgress(events.Add), TestContext);
        Assert.Equal(0, events.Last().Updated);
        Assert.Equal(1, events.Last().Processed);
        Assert.False(tracking.HasPendingScan());
        Assert.Single(Search(reopened, "restartneedle").Hits);
    }

    [Fact]
    public async Task PendingFullScanIsNotLostWhenScheduledSubsetStarts()
    {
        var other = Directory.CreateDirectory(Path.Combine(workspace, "other")).FullName;
        Directory.CreateDirectory(Source);
        using var index = OpenIndex();
        var tracking = new TrackingStore(Data);
        var options = Options with { Folders = [Source, other] };
        var coordinator = new IndexCoordinator(index, tracking);
        await coordinator.ReconcileAsync(options, null, TestContext);
        File.WriteAllText(Path.Combine(other, "pending.txt"), "pendingneedle");
        tracking.RequestScan();
        await coordinator.ReconcileAsync(options, null, TestContext, [Source]);
        Assert.Single(Search(index, "pendingneedle").Hits);
        Assert.False(tracking.HasPendingScan());
    }

    [Fact]
    public void PresentationPreferencesSurviveReloadAndOldSettingsHaveDefaults()
    {
        var store = new SettingsStore(Data);
        Assert.Equal(250, store.Load().PageSize);
        Assert.True(store.Load().ShowModified);
        store.Save(new Settings { PageSize = 100, ShowPath = false, ShowLocation = false, ShowExcerpt = false });
        var loaded = store.Load();
        Assert.Equal(100, loaded.PageSize);
        Assert.False(loaded.ShowPath);
        Assert.False(loaded.ShowLocation);
        Assert.False(loaded.ShowExcerpt);
        Assert.True(loaded.ShowModified);
    }

    private sealed class InlineProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }

    private void WriteFixtures()
    {
        File.WriteAllText(FilePath("notes.txt"), "harbour arrival");
        File.WriteAllText(FilePath("maintenance.md"), "# winch support");
        File.WriteAllText(FilePath("voyage.csv"), "label,value\ndraught,12");
        File.WriteAllText(FilePath("procedure.html"), "<html><body><p>drop</p><p>anchors</p><script>scriptneedle</script></body></html>");
        var pdf = new PdfDocumentBuilder();
        var font = pdf.AddStandard14Font(Standard14Font.Helvetica);
        pdf.AddPage(612, 792).AddText("emergency drills", 12, new PdfPoint(36, 710), font);
        pdf.AddPage(612, 792).AddText("emergency towing", 12, new PdfPoint(36, 710), font);
        File.WriteAllBytes(FilePath("manual.pdf"), pdf.Build());

        using (var word = WordprocessingDocument.Create(FilePath("survey.docx"), DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            word.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("waterline")))));

        using (var sheet = SpreadsheetDocument.Create(FilePath("metrics.xlsx"), DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbook = sheet.AddWorkbookPart();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
            {
                CellReference = "B2", DataType = S.CellValues.InlineString,
                InlineString = new(new S.Text("ballast valve"))
            }) { RowIndex = 2 }));
            workbook.Workbook = new S.Workbook(new S.Sheets(new S.Sheet
            {
                Name = "Inspection", SheetId = 1, Id = workbook.GetIdOfPart(worksheet)
            }));
        }

        using var deck = PresentationDocument.Create(FilePath("slides.pptx"), DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
        var presentation = deck.AddPresentationPart();
        var slide = presentation.AddNewPart<SlidePart>();
        slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(new P.Shape(
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("docking"))))))));
        presentation.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId
        {
            Id = 256, RelationshipId = presentation.GetIdOfPart(slide)
        }));
    }

    private static CancellationToken TestContext => CancellationToken.None;
    private static SearchResults Search(ISearchIndex index, string query, FileFilter filter = FileFilter.All) =>
        index.Search(new(query, filter, SortOrder.Relevance), TestContext);

    public void Dispose() => Directory.Delete(workspace, recursive: true);
}

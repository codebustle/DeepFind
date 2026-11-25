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

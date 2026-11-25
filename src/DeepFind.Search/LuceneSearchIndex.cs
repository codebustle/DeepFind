using System.Text.Json;
using System.Text.RegularExpressions;
using DeepFind.Core;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace DeepFind.Search;

public sealed partial class LuceneSearchIndex : ISearchIndex
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;
    private readonly FSDirectory directory;
    private readonly StandardAnalyzer analyzer = new(Version, Lucene.Net.Analysis.Util.CharArraySet.Empty);
    private readonly object sync = new();
    private IndexWriter writer;
    private readonly SearcherManager readers;

    public LuceneSearchIndex(string path)
    {
        directory = FSDirectory.Open(System.IO.Directory.CreateDirectory(path));
        try
        {
            writer = OpenWriter();
            writer.Commit();
            readers = new SearcherManager(directory, null);
        }
        catch
        {
            writer?.Rollback();
            directory.Dispose();
            analyzer.Dispose();
            throw;
        }
    }

    public void Upsert(IndexedDocument document)
    {
        var file = document.File;
        var name = Path.GetFileName(file.Path);
        var fields = new Document
        {
            new StringField("id", file.Id, Field.Store.YES),
            new TextField("name", QueryLanguage.NormalizePath(name), Field.Store.NO),
            new StringField("nameExact", name.ToLowerInvariant(), Field.Store.NO),
            new TextField("path", QueryLanguage.NormalizePath(file.Path), Field.Store.NO),
            new StoredField("originalPath", file.Path),
            new StringField("pathSort", file.Path.ToLowerInvariant(), Field.Store.NO),
            new StringField("extension", file.Extension, Field.Store.NO),
            new Int64Field("size", file.Size, Field.Store.YES),
            new Int64Field("modified", file.ModifiedTicks, Field.Store.YES),
            new TextField("content", string.Join("\n", document.Parts.Select(part => part.Text)), Field.Store.NO),
            new StoredField("parts", JsonSerializer.Serialize(document.Parts))
        };
        lock (sync)
            writer.UpdateDocument(new Term("id", file.Id), fields);
    }

    public void Delete(string id) { lock (sync) writer.DeleteDocuments(new Term("id", id)); }
    public void Clear() { lock (sync) writer.DeleteAll(); }
    public void Commit()
    {
        lock (sync)
        {
            writer.Commit();
            readers.MaybeRefreshBlocking();
        }
    }

    public void Rollback()
    {
        lock (sync)
        {
            writer.Rollback();
            writer = OpenWriter();
        }
    }

    public IReadOnlyDictionary<string, FileVersion> GetVersions()
    {
        using var reader = OpenReader();
        var live = MultiFields.GetLiveDocs(reader);
        var result = new Dictionary<string, FileVersion>(StringComparer.Ordinal);
        var fields = new HashSet<string> { "id", "originalPath", "size", "modified" };
        for (var i = 0; i < reader.MaxDoc; i++)
        {
            if (live is not null && !live.Get(i))
                continue;
            var doc = reader.Document(i, fields);
            result[doc.Get("id")] = ReadFile(doc);
        }
        return result;
    }

    public SearchResults Search(SearchRequest request, CancellationToken cancellationToken)
    {
        var (query, highlights, phrases) = QueryLanguage.Parse(request.Query, analyzer);
        var searcher = readers.Acquire();
        try
        {
            return SearchCommitted(searcher, query, highlights, phrases, request, cancellationToken);
        }
        finally { readers.Release(searcher); }
    }

    private static SearchResults SearchCommitted(IndexSearcher searcher, Query query, string[] highlights, string[] phrases,
        SearchRequest request, CancellationToken cancellationToken)
    {
        var filter = Filter(request.Filter);
        var limit = Math.Clamp(request.Limit, 1, 1000);
        var sort = request.Sort switch
        {
            SortOrder.Modified => new SortField("modified", SortFieldType.INT64, true),
            SortOrder.Name => new SortField("nameExact", SortFieldType.STRING),
            SortOrder.Size => new SortField("size", SortFieldType.INT64, true),
            SortOrder.Path => new SortField("pathSort", SortFieldType.STRING),
            _ => null
        };
        cancellationToken.ThrowIfCancellationRequested();
        TopDocsCollector<ScoreDoc> relevance = TopScoreDocCollector.Create(limit, false);
        var sorted = sort is null ? null : TopFieldCollector.Create(
            new Sort(sort, new SortField("id", SortFieldType.STRING)), limit, false, false, false, false);
        ICollector collector = sorted is null ? relevance : sorted;
        searcher.Search(query, filter, Collector.NewAnonymous(collector.SetScorer, doc =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            collector.Collect(doc);
        }, collector.SetNextReader, () => collector.AcceptsDocsOutOfOrder));
        var top = sorted is null ? relevance.GetTopDocs() : sorted.GetTopDocs();

        var words = MatchTerms(highlights);
        var exact = phrases.Length == 0 ? null : new Regex(
            string.Join("|", phrases.Select(phrase => string.Join(@"\s+", phrase.Split(' ').Select(Regex.Escape)))),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hits = new List<SearchHit>();
        foreach (var score in top.ScoreDocs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = searcher.Doc(score.Doc);
            var parts = JsonSerializer.Deserialize<TextPart[]>(doc.Get("parts")) ?? [];
            var (snippet, location) = Snippet(parts, exact, words);
            hits.Add(new(ReadFile(doc), snippet, location, highlights));
        }
        return new(top.TotalHits, hits);
    }

    public string ReadPreview(SearchHit hit)
    {
        var searcher = readers.Acquire();
        try
        {
            var match = searcher.Search(new TermQuery(new Term("id", hit.File.Id)), 1).ScoreDocs.FirstOrDefault();
            if (match is null)
                return "";
            var doc = searcher.Doc(match.Doc);
            if (ReadFile(doc) != hit.File)
                return hit.Snippet;
            var parts = JsonSerializer.Deserialize<TextPart[]>(doc.Get("parts")) ?? [];
            var candidates = parts.Where(part => part.Location == hit.Location).ToArray();
            return Snippet(candidates.Length > 0 ? candidates : parts, null, MatchTerms(hit.Highlights),
                context: 400, length: 2400).Text;
        }
        finally { readers.Release(searcher); }
    }

    private static Regex? MatchTerms(IReadOnlyList<string> highlights) => highlights.Count == 0 ? null : new Regex(
        $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", highlights.OrderByDescending(term => term.Length).Select(Regex.Escape))})(?![\p{{L}}\p{{N}}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (string Text, string? Location) Snippet(TextPart[] parts, Regex? exact, Regex? words,
        int context = 65, int length = 220)
    {
        if (parts.Length == 0)
            return ("", null);

        var (selected, offset) = FindMatch(parts, exact) ?? FindMatch(parts, words) ?? (parts[0], 0);
        var start = Math.Max(0, offset - context);
        while (start > 0 && !char.IsWhiteSpace(selected.Text[start - 1]) && offset - start < context + 25)
            start--;
        var end = Math.Min(selected.Text.Length, Math.Max(start + length, offset + 75));
        var excerpt = Whitespace().Replace(selected.Text[start..end], " ").Trim();
        return ((start > 0 ? "…" : "") + excerpt + (end < selected.Text.Length ? "…" : ""), selected.Location);
    }

    private static (TextPart Part, int Offset)? FindMatch(TextPart[] parts, Regex? pattern)
    {
        if (pattern is null)
            return null;
        foreach (var part in parts)
        {
            var match = pattern.Match(part.Text);
            if (match.Success)
                return (part, match.Index);
        }
        return null;
    }

    private static Filter? Filter(FileFilter filter)
    {
        string[] extensions = filter switch
        {
            FileFilter.Pdf => ["pdf"],
            FileFilter.Excel => ["xlsx", "csv"],
            FileFilter.Documents => ["docx", "pptx", "txt", "md", "html", "htm", "pdf"],
            _ => []
        };
        if (extensions.Length == 0)
            return null;
        var query = new BooleanQuery();
        foreach (var extension in extensions)
            query.Add(new TermQuery(new Term("extension", extension)), Occur.SHOULD);
        return new QueryWrapperFilter(query);
    }

    private static FileVersion ReadFile(Document doc) =>
        new(doc.Get("originalPath"), doc.GetField("size").GetInt64Value()!.Value,
            doc.GetField("modified").GetInt64Value()!.Value);

    private IndexWriter OpenWriter() => new(directory, new IndexWriterConfig(Version, analyzer));
    private DirectoryReader OpenReader()
    {
        lock (sync)
            return DirectoryReader.Open(directory);
    }

    public void Dispose()
    {
        lock (sync)
        {
            readers.Dispose();
            writer.Rollback();
            directory.Dispose();
            analyzer.Dispose();
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

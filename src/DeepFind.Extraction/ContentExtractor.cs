using System.IO.Compression;
using System.Text;
using AngleSharp.Html.Parser;
using DeepFind.Core;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DeepFind.Extraction;

public sealed class ContentExtractor : IContentExtractor
{
    public const int MaxCharacters = 1_000_000;
    private const long MaxExpandedBytes = 150L * 1024 * 1024;

    public bool CanExtract(string extension) =>
        extension is "txt" or "md" or "csv" or "html" or "htm" or "pdf" or "docx" or "xlsx" or "pptx";

    public ExtractedDocument Extract(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension is "docx" or "xlsx" or "pptx")
            CheckOfficePackage(stream, cancellationToken);

        var budget = MaxCharacters;
        var parts = new List<TextPart>();
        foreach (var part in ReadParts(stream, extension, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = part.Text.Replace('\0', ' ').Trim();
            if (text.Length == 0)
                continue;
            if (text.Length >= budget)
            {
                parts.Add(part with { Text = text[..budget] });
                return new(parts, "Text limit reached. Search covers the first million characters.");
            }

            // Bound location metadata even for sheets containing many tiny values.
            if (parts.Count == 20_000)
                return new(parts, "Location limit reached. Search covers the first 20,000 text segments.");
            parts.Add(part with { Text = text });
            budget -= text.Length;
        }

        return new(parts, extension == "pdf" && parts.Count == 0 ? "No searchable text. The PDF may be scanned." : null);
    }

    private static IEnumerable<TextPart> ReadParts(Stream stream, string extension, CancellationToken token)
    {
        switch (extension)
        {
            case "pdf":
                using (var pdf = PdfDocument.Open(stream))
                    foreach (var page in pdf.GetPages())
                    {
                        token.ThrowIfCancellationRequested();
                        yield return new(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page),
                            $"Page {page.Number}");
                    }
                break;
            case "docx":
                using (var word = WordprocessingDocument.Open(stream, false))
                {
                    var part = word.MainDocumentPart;
                    if (part?.Document?.Body is { } body)
                        foreach (var paragraph in body.Descendants<W.Paragraph>())
                            yield return new(paragraph.InnerText);
                    if (part is not null)
                        foreach (var extra in part.HeaderParts.Select(x => x.Header)
                            .Cast<DocumentFormat.OpenXml.OpenXmlElement?>().Concat(part.FooterParts.Select(x => x.Footer)).OfType<DocumentFormat.OpenXml.OpenXmlElement>())
                            foreach (var paragraph in extra.Descendants<W.Paragraph>())
                                yield return new(paragraph.InnerText);
                }
                break;
            case "pptx":
                using (var deck = PresentationDocument.Open(stream, false))
                {
                    var presentation = deck.PresentationPart;
                    if (presentation?.Presentation?.SlideIdList is not { } slideIds)
                        break;
                    var slideNumber = 0;
                    foreach (var slide in slideIds.Elements<P.SlideId>())
                    {
                        token.ThrowIfCancellationRequested();
                        slideNumber++;
                        if (slide.RelationshipId?.Value is { } id && presentation.GetPartById(id) is SlidePart { Slide: { } slideContent })
                            foreach (var paragraph in slideContent.Descendants<A.Paragraph>())
                                yield return new(paragraph.InnerText, $"Slide {slideNumber}");
                    }
                }
                break;
            case "xlsx":
                using (var spreadsheet = SpreadsheetDocument.Open(stream, false))
                {
                    var workbook = spreadsheet.WorkbookPart;
                    if (workbook?.Workbook?.Sheets is not { } sheets)
                        break;
                    var shared = workbook.SharedStringTablePart?.SharedStringTable?
                        .Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
                    foreach (var sheet in sheets.Elements<Sheet>())
                    {
                        if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart { Worksheet: { } worksheet })
                            continue;
                        foreach (var cell in worksheet.Descendants<Cell>())
                        {
                            token.ThrowIfCancellationRequested();
                            var value = cell.CellValue?.InnerText ?? cell.InlineString?.InnerText ?? "";
                            if (cell.DataType?.Value == CellValues.SharedString)
                                value = int.TryParse(value, out var index) && (uint)index < (uint)shared.Length ? shared[index] : "";
                            yield return new(value, $"{sheet.Name?.Value}!{cell.CellReference?.Value}");
                        }
                    }
                }
                break;
            case "txt":
            case "md":
            case "csv":
                yield return new(ReadText(stream, token));
                break;
            case "html":
            case "htm":
                using (var html = new HtmlParser().ParseDocument(ReadText(stream, token)))
                {
                    foreach (var node in html.QuerySelectorAll("script, style, noscript, template"))
                        node.Remove();
                    if (html.Title is { Length: > 0 } title)
                        yield return new(title);
                    if (html.Body is { } content)
                    {
                        // Delimit blocks so adjacent HTML tags cannot merge words.
                        foreach (var block in content.QuerySelectorAll("p, div, br, li, h1, h2, h3, h4, h5, h6, td, th, section"))
                            block.Parent?.InsertBefore(html.CreateTextNode(" "), block.NextSibling);
                        yield return new(content.TextContent);
                    }
                }
                break;
        }
    }

    private static string ReadText(Stream stream, CancellationToken token)
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), Encoding.GetEncoding(1252) })
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding, true, 4096, leaveOpen: true);
            var buffer = new char[8192];
            var result = new StringBuilder();
            try
            {
                while (result.Length <= MaxCharacters)
                {
                    token.ThrowIfCancellationRequested();
                    var count = reader.Read(buffer, 0, Math.Min(buffer.Length, MaxCharacters + 1 - result.Length));
                    if (count == 0)
                        break;
                    result.Append(buffer, 0, count);
                }
                return result.ToString();
            }
            catch (DecoderFallbackException) when (encoding is UTF8Encoding) { }
        }
        throw new InvalidDataException("Text encoding could not be read.");
    }

    private static void CheckOfficePackage(Stream stream, CancellationToken token)
    {
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length > MaxExpandedBytes - expanded)
                    throw new InvalidDataException("Expanded Office document exceeds the resource limit.");
                expanded += entry.Length;
            }
        }
        stream.Position = 0;
    }

    static ContentExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

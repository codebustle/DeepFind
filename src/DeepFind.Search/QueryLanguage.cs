using System.Text.RegularExpressions;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace DeepFind.Search;

internal static partial class QueryLanguage
{
    internal const string Help = "Use words, quotes for an exact phrase, AND, OR, filename:, or ext:.";
    private const string Content = "content";

    [GeneratedRegex("""\G\s*(?:(?<field>[a-zA-Z]+):)?(?:"(?<phrase>[^"]+)"|(?<word>[^\s":()~*?\[\]{}^+!|&]+))(?=\s|$)""")]
    private static partial Regex Clauses();

    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}]+")]
    private static partial Regex PathSeparators();

    internal static string NormalizePath(string path) => PathSeparators().Replace(path, " ");

    public static (Query Query, string[] Highlights, string[] Phrases) Parse(string input, Analyzer analyzer)
    {
        if (input.Length > 512)
            throw new FormatException("Search queries can contain up to 512 characters.");
        if (string.IsNullOrWhiteSpace(input))
            return (new MatchAllDocsQuery(), [], []);

        var disjunction = new BooleanQuery();
        var conjunction = new BooleanQuery();
        var restrictions = new BooleanQuery();
        var highlights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phrases = new List<string>();
        var position = 0;
        var needsOperand = true;

        foreach (Match match in Clauses().Matches(input.Trim()))
        {
            position += match.Length;
            var field = match.Groups["field"].Value.ToLowerInvariant();
            var quoted = match.Groups["phrase"].Success;
            var value = match.Groups[quoted ? "phrase" : "word"].Value;
            if (field == "" && !quoted && value is "OR" or "AND")
            {
                if (needsOperand)
                    throw new FormatException(Help);
                if (value == "OR")
                {
                    if (conjunction.Clauses.Count == 0)
                        throw new FormatException("OR needs a search term on both sides.");
                    disjunction.Add(conjunction, Occur.SHOULD);
                    conjunction = new BooleanQuery();
                }
                needsOperand = true;
                continue;
            }

            if (field == "ext" && !quoted && value.TrimStart('.').All(char.IsLetterOrDigit) &&
                value.TrimStart('.').Length > 0)
            {
                restrictions.Add(new TermQuery(new Term("extension", value.TrimStart('.').ToLowerInvariant())), Occur.MUST);
                needsOperand = false;
                continue;
            }

            if (field is not ("" or "filename") || (!quoted && value is "NOT"))
                throw new FormatException(Help);
            var tokens = Tokenize(analyzer, value);
            var pathTokens = Tokenize(analyzer, NormalizePath(value));
            if (tokens.Length == 0 || pathTokens.Length == 0)
                throw new FormatException("Enter a searchable word or filename.");
            highlights.UnionWith(tokens);
            if (!quoted || field == "filename")
                highlights.UnionWith(pathTokens);
            if (quoted && field == "")
                phrases.Add(string.Join(" ", tokens));
            var alternatives = new BooleanQuery();
            if (field == "")
                alternatives.Add(ForField(Content, tokens, quoted, quoted ? 5 : 1), Occur.SHOULD);
            if (!quoted || field == "filename")
            {
                alternatives.Add(ForField("name", pathTokens, quoted, 3), Occur.SHOULD);
                alternatives.Add(new TermQuery(new Term("nameExact", value.ToLowerInvariant())) { Boost = 20 }, Occur.SHOULD);
            }
            if (field == "" && !quoted)
                alternatives.Add(ForField("path", pathTokens, false, 0.5f), Occur.SHOULD);
            conjunction.Add(alternatives, Occur.MUST);
            needsOperand = false;
        }

        if (position != input.Trim().Length || needsOperand || (conjunction.Clauses.Count == 0 && disjunction.Clauses.Count > 0))
            throw new FormatException(Help);
        if (conjunction.Clauses.Count > 0)
        {
            disjunction.Add(conjunction, Occur.SHOULD);
            restrictions.Add(disjunction, Occur.MUST);
        }
        return (restrictions, highlights.ToArray(), phrases.ToArray());
    }

    private static Query ForField(string field, string[] tokens, bool phrase, float boost)
    {
        if (tokens.Length == 1)
            return new TermQuery(new Term(field, tokens[0])) { Boost = boost };
        if (phrase)
        {
            var query = new PhraseQuery { Boost = boost };
            foreach (var token in tokens)
                query.Add(new Term(field, token));
            return query;
        }

        var words = new BooleanQuery { Boost = boost };
        foreach (var token in tokens)
            words.Add(new TermQuery(new Term(field, token)), Occur.MUST);
        return words;
    }

    private static string[] Tokenize(Analyzer analyzer, string value)
    {
        using var stream = analyzer.GetTokenStream(Content, new StringReader(value));
        var term = stream.AddAttribute<ICharTermAttribute>();
        var tokens = new List<string>();
        stream.Reset();
        while (stream.IncrementToken())
            tokens.Add(term.ToString());
        stream.End();
        return tokens.ToArray();
    }
}

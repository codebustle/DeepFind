using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DeepFind.App;

public static class HighlightText
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(HighlightText), new PropertyMetadata("", Refresh));
    public static readonly DependencyProperty PrefixProperty = DependencyProperty.RegisterAttached(
        "Prefix", typeof(string), typeof(HighlightText), new PropertyMetadata("", Refresh));
    public static readonly DependencyProperty TermsProperty = DependencyProperty.RegisterAttached(
        "Terms", typeof(IReadOnlyList<string>), typeof(HighlightText), new PropertyMetadata(null, Refresh));

    public static string GetValue(DependencyObject element) => (string)element.GetValue(ValueProperty);
    public static void SetValue(DependencyObject element, string value) => element.SetValue(ValueProperty, value);
    public static string GetPrefix(DependencyObject element) => (string)element.GetValue(PrefixProperty);
    public static void SetPrefix(DependencyObject element, string value) => element.SetValue(PrefixProperty, value);
    public static IReadOnlyList<string>? GetTerms(DependencyObject element) =>
        (IReadOnlyList<string>?)element.GetValue(TermsProperty);
    public static void SetTerms(DependencyObject element, IReadOnlyList<string> value) => element.SetValue(TermsProperty, value);

    private static void Refresh(DependencyObject sender, DependencyPropertyChangedEventArgs _)
    {
        if (sender is not TextBlock text) return;
        text.Inlines.Clear();
        var value = GetValue(text) ?? "";
        var prefix = GetPrefix(text);
        if (!string.IsNullOrEmpty(prefix))
            text.Inlines.Add(new Run(prefix) { FontWeight = FontWeights.SemiBold });

        if (GetTerms(text) is not { Count: > 0 } terms)
        {
            text.Inlines.Add(new Run(value));
            return;
        }

        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", terms.OrderByDescending(term => term.Length).Select(Regex.Escape))})(?![\p{{L}}\p{{N}}])";
        var offset = 0;
        foreach (Match match in Regex.Matches(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (match.Index > offset)
                text.Inlines.Add(new Run(value[offset..match.Index]));
            var highlight = new Run(match.Value) { FontWeight = FontWeights.SemiBold, TextDecorations = TextDecorations.Underline };
            highlight.SetResourceReference(TextElement.BackgroundProperty, "HighlightFill");
            highlight.SetResourceReference(TextElement.ForegroundProperty, "HighlightInk");
            text.Inlines.Add(highlight);
            offset = match.Index + match.Length;
        }
        if (offset < value.Length)
            text.Inlines.Add(new Run(value[offset..]));
    }
}

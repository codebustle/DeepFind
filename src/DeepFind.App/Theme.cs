using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeepFind.App;

internal static class Theme
{
    public static void Apply(string choice)
    {
        var palette = Application.Current.Resources;
        if (SystemParameters.HighContrast)
        {
            palette["Surface"] = SystemColors.WindowBrush;
            palette["Ink"] = SystemColors.WindowTextBrush;
            palette["Secondary"] = SystemColors.WindowTextBrush;
            palette["ControlSurface"] = SystemColors.WindowBrush;
            palette["Line"] = SystemColors.WindowTextBrush;
            palette["FocusEdge"] = SystemColors.HighlightBrush;
            palette["HighlightFill"] = SystemColors.HighlightBrush;
            palette["HighlightInk"] = SystemColors.HighlightTextBrush;
            return;
        }

        var dark = choice == "Dark" || choice == "System" &&
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        Set("Surface", "#141516", "#FAFAFA");
        Set("Ink", "#F2F2F2", "#181818");
        Set("Secondary", "#B9B9BB", "#555558");
        Set("ControlSurface", "#252627", "#EFEFF0");
        Set("Line", "#454648", "#C3C3C7");
        Set("FocusEdge", "#E2E2E4", "#414147");
        Set("HighlightFill", "#444448", "#E4E4EA");
        Set("HighlightInk", "#FAFAFA", "#171718");

        void Set(string key, string darkValue, string lightValue)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkValue : lightValue));
            brush.Freeze();
            palette[key] = brush;
        }
    }
}

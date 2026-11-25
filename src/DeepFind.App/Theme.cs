using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeepFind.App;

internal static class Theme
{
    private static bool dark;
    private static bool highContrast;
    private static Color titleColor;
    private static Color inkColor;

    public static void Apply(string choice)
    {
        var application = Application.Current;
        highContrast = SystemParameters.HighContrast;
        dark = choice == "Dark" || choice == "System" &&
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

        // Fluent supplies the platform's light/dark/contrast rendering for native controls
        // such as scrollbars. Custom DeepFind controls use the palette below.
#pragma warning disable WPF0001
        application.ThemeMode = highContrast ? ThemeMode.System : dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var palette = application.Resources;
        if (highContrast)
        {
            palette["Surface"] = SystemColors.WindowBrush;
            palette["Ink"] = SystemColors.WindowTextBrush;
            palette["Secondary"] = SystemColors.WindowTextBrush;
            palette["ControlSurface"] = SystemColors.ControlBrush;
            palette["Line"] = SystemColors.WindowTextBrush;
            palette["FocusEdge"] = SystemColors.HighlightBrush;
            palette["HighlightFill"] = SystemColors.HighlightBrush;
            palette["HighlightInk"] = SystemColors.HighlightTextBrush;
            palette["Panel"] = SystemColors.WindowBrush;
            palette["Title"] = SystemColors.ControlBrush;
            palette["Primary"] = SystemColors.ControlBrush;
            palette["OnPrimary"] = SystemColors.ControlTextBrush;
            palette["Selected"] = SystemColors.ControlBrush;
            palette["Menu"] = SystemColors.MenuBrush;
        }
        else
        {
            Set("Surface", "#141516", "#FAFAFA");
            Set("Ink", "#F2F2F2", "#181818");
            Set("Secondary", "#B9B9BB", "#555558");
            Set("ControlSurface", "#252627", "#EFEFF0");
            Set("Line", "#454648", "#C3C3C7");
            Set("FocusEdge", "#E2E2E4", "#414147");
            Set("HighlightFill", "#444448", "#E4E4EA");
            Set("HighlightInk", "#FAFAFA", "#171718");
            Set("Panel", "#1D1E20", "#FFFFFF");
            Set("Title", "#1C1D1F", "#F3F3F4");
            Set("Primary", "#48494C", "#242426");
            Set("OnPrimary", "#F2F2F2", "#FFFFFF");
            Set("Selected", "#343537", "#E9E9EB");
            Set("Menu", "#252627", "#FFFFFF");
            titleColor = ((SolidColorBrush)palette["Title"]).Color;
            inkColor = ((SolidColorBrush)palette["Ink"]).Color;
        }

        foreach (Window window in application.Windows)
            ApplyWindowFrame(window);

        void Set(string key, string darkValue, string lightValue)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkValue : lightValue));
            brush.Freeze();
            palette[key] = brush;
        }
    }

    // Preserve native caption buttons, window dragging, snapping and resizing.
    // DWM caption colors are supported on Windows 11; older builds retain the
    // supported native frame behavior if the optional attributes are unavailable.
    public static void AttachWindow(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyWindowFrame(window);
        ApplyWindowFrame(window);
    }

    private static void ApplyWindowFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        const int useImmersiveDarkMode = 20;
        const int cornerPreference = 33;
        const int captionColor = 35;
        const int textColor = 36;
        SetAttribute(handle, useImmersiveDarkMode, dark && !highContrast ? 1 : 0);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        const int defaultColor = unchecked((int)0xFFFFFFFF);
        SetAttribute(handle, captionColor, highContrast ? defaultColor : ToColorRef(titleColor));
        SetAttribute(handle, textColor, highContrast ? defaultColor : ToColorRef(inkColor));
        SetAttribute(handle, cornerPreference, highContrast ? 0 : 2);
    }

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private static void SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        // An unsupported optional attribute returns an HRESULT; it must not block the app.
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, (uint)sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, uint size);
}

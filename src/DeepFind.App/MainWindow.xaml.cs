using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeepFind.Core;
using Microsoft.Win32;

namespace DeepFind.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private bool closing;
    private bool allowClose;

    public MainWindow(MainViewModel model)
    {
        this.model = model;
        Theme.Apply(model.Settings.Theme);
        InitializeComponent();
        Theme.AttachWindow(this);
        DataContext = model;
        SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
    }

    public SortOrder[] SortOptions { get; } = Enum.GetValues<SortOrder>();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        await model.StartAsync();
    }

    private void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => Theme.Apply(model.Settings.Theme));

    private void OnSearch(object sender, RoutedEventArgs e) => model.QueueSearch(delay: false);
    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        // Clear only the query; keep the selected filter and sort and refresh results immediately.
        model.Query = string.Empty;
        model.QueueSearch(delay: false);
        SearchBox.Focus();
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
            model.Filter = Enum.Parse<FileFilter>(tag);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new IndexWindow(model) { Owner = this };
        dialog.ShowDialog();
        Theme.Apply(model.Settings.Theme);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.L or Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (e.Key == Key.Escape)
            model.CancelSearch();
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocusWithin)
            model.QueueSearch(delay: false);
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && ResultsList.HasItems)
        {
            ResultsList.SelectedIndex = 0;
            ResultsList.ScrollIntoView(ResultsList.Items[0]);
            ResultsList.UpdateLayout();
            (ResultsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        }
        else if (ResultsList.IsKeyboardFocusWithin && ResultsList.SelectedItem is ResultRow row)
        {
            if (e.Key == Key.Enter)
                Launch(row, reveal: false);
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
                Copy(row);
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P)
                Preview(row);
            else
                return;
        }
        else
            return;

        e.Handled = true;
    }

    private void OnResultRightClick(object sender, MouseButtonEventArgs e)
    {
        // A context-menu command must act on the row under the pointer.
        if (e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(ResultsList, target) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(ResultsList, target) is ListBoxItem { DataContext: ResultRow row })
            Launch(row, reveal: false);
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultRow row) Launch(row, reveal: false);
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultRow row) Launch(row, reveal: true);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultRow row) Copy(row);
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is ResultRow row) Preview(row);
    }

    private void Copy(ResultRow row)
    {
        try { Clipboard.SetText(row.Hit.File.Path); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try copying the path again.", Title);
        }
    }

    private void Launch(ResultRow row, bool reveal)
    {
        var path = row.Hit.File.Path;
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show(this, "The file is currently unavailable.", Title);
            return;
        }

        try
        {
            var launch = reveal ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" };
            Process.Start(launch)?.Dispose();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "Windows could not open the selected file.", Title);
        }
    }

    private void Preview(ResultRow row)
    {
        var dialog = new Window
        {
            Owner = this, Title = row.Name, Width = 620, Height = 305, MinWidth = 300, MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, FontSize = 15
        };
        Theme.AttachWindow(dialog);
        dialog.SetResourceReference(BackgroundProperty, "Surface");
        dialog.SetResourceReference(ForegroundProperty, "Ink");
        var content = new DockPanel { Margin = new Thickness(20) };
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80 };
        close.SetResourceReference(FrameworkElement.StyleProperty, "SoftButton");
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0), Child = close };
        footer.SetResourceReference(Border.BorderBrushProperty, "Line");
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        var path = new TextBlock { Text = row.PathText, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        path.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8), Margin = new Thickness(0, 0, 0, 9), Child = path };
        header.SetResourceReference(Border.BorderBrushProperty, "Line");
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        string text;
        try { text = model.ReadPreview(row.Hit); }
        catch (Exception error) when (error is System.IO.IOException or System.Text.Json.JsonException)
        {
            text = row.Hit.Snippet;
        }
        HighlightText.SetValue(preview, text.Length == 0 ? "No indexed text is available for this file." : text);
        HighlightText.SetPrefix(preview, row.Location);
        HighlightText.SetTerms(preview, row.Hit.Highlights);
        content.Children.Add(new ScrollViewer { Content = preview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = content;
        dialog.ShowDialog();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        SystemEvents.UserPreferenceChanged -= OnPreferencesChanged;
        IsEnabled = false;
        try { await model.CloseAsync(); }
        finally { allowClose = true; Close(); }
    }
}

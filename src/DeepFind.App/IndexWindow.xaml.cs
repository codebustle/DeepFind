using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeepFind.Core;
using Microsoft.Win32;

namespace DeepFind.App;

public partial class IndexWindow : Window
{
    private readonly MainViewModel model;
    private readonly ObservableCollection<string> folders;
    private bool acting;

    public IndexWindow(MainViewModel model)
    {
        this.model = model;
        InitializeComponent();
        Theme.AttachWindow(this);
        DataContext = model;
        folders = new(model.Settings.Folders);
        FolderList.ItemsSource = folders;
        MaxSize.Text = model.Settings.MaxMegabytes.ToString();
        LocalHours.Text = model.Settings.LocalHours.ToString();
        NetworkHours.Text = model.Settings.NetworkHours.ToString();
        Exclusions.Text = string.Join(Environment.NewLine, model.Settings.Exclusions);
        IncludeOther.IsChecked = model.Settings.IncludeOtherFiles;
        IndexLocation.Text = model.Settings.IndexLocation ?? "";
        StorageNote.Text = $"Current index: {model.IndexDirectory}\nThe index stores copies of extracted text. Clear it before changing storage location to remove the old copies.";
        ThemeChoice.ItemsSource = new[] { "System", "Light", "Dark" };
        ThemeChoice.SelectedItem = model.Settings.Theme;
        UpdatePauseButton();
        Closing += (_, e) => e.Cancel = acting;
    }

    private void OnIntervalsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || NetworkInterval is null)
            return;

        // At narrow window widths the two related interval fields stack vertically.
        var narrow = e.NewSize.Width < 480;
        Grid.SetColumn(NetworkInterval, narrow ? 0 : 1);
        Grid.SetRow(NetworkInterval, narrow ? 1 : 0);
        IntervalsGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        NetworkInterval.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(16, 0, 0, 0);
    }

    private void UpdatePauseButton() => PauseButton.Content = model.Settings.Paused ? "Resume" : "Pause";

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var open = new OpenFolderDialog { Title = "Choose folders to index", Multiselect = true };
        if (open.ShowDialog(this) != true)
            return;
        foreach (var folder in open.FolderNames)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                folders.Add(path);
        }
        Feedback.Text = "Apply changes to index the new folders.";
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not string folder)
            return;
        folders.Remove(folder);
        Feedback.Text = "Apply changes to remove this folder and its indexed text.";
    }

    private void OnBrowseIndex(object sender, RoutedEventArgs e)
    {
        var open = new OpenFolderDialog { Title = "Choose an index storage parent folder" };
        if (open.ShowDialog(this) == true)
            IndexLocation.Text = open.FolderName;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxSize.Text, out var limit) || limit is < 1 or > 1024 ||
            !int.TryParse(LocalHours.Text, out var local) || local is < 1 or > 720 ||
            !int.TryParse(NetworkHours.Text, out var network) || network is < 1 or > 720)
        {
            Feedback.Text = "Enter a file size from 1–1024 MB and check intervals from 1–720 hours.";
            return;
        }

        var directory = IndexLocation.Text.Trim();
        if (directory.Length > 0 && (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)))
        {
            Feedback.Text = "Choose an existing index storage parent folder or leave the field empty to use the default.";
            return;
        }

        var settings = model.Settings with
        {
            Folders = folders.ToArray(),
            MaxMegabytes = limit,
            IncludeOtherFiles = IncludeOther.IsChecked == true,
            LocalHours = local,
            NetworkHours = network,
            Theme = ThemeChoice.SelectedItem as string ?? "System",
            IndexLocation = directory.Length == 0 ? null : directory,
            Exclusions = Exclusions.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
        await ExecuteAsync(async () =>
        {
            await model.ApplySettingsAsync(settings);
            Theme.Apply(settings.Theme);
            Feedback.Text = string.Equals(model.StartupLocation, model.IndexDirectory, StringComparison.OrdinalIgnoreCase)
                ? "Settings saved." : "Settings saved. Restart DeepFind to use the new index location.";
        });
    }

    private async void OnPause(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(() => model.SetPausedAsync(!model.Settings.Paused));

    private void OnUpdate(object sender, RoutedEventArgs e) => StartUpdate();
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (IssuesList.SelectedItem is FileState state)
            StartUpdate(state.File.Path);
        else
            Feedback.Text = "Select a file notice to retry.";
    }

    private void StartUpdate(string? path = null)
    {
        if (model.Settings.Paused)
            Feedback.Text = "Resume indexing before starting an update.";
        else if (model.Busy)
            Feedback.Text = "An index update is already running.";
        else
        {
            Feedback.Text = "";
            _ = model.UpdateAsync(path);
        }
    }

    private async void OnRebuild(object sender, RoutedEventArgs e)
    {
        if (ConfirmationDialog.Ask(this, "Clear the current index and scan all selected folders again?"))
            await ExecuteAsync(() => model.ResetIndexAsync(rebuild: true));
    }

    private async void OnClear(object sender, RoutedEventArgs e)
    {
        if (ConfirmationDialog.Ask(this, "Remove all indexed content and pause indexing? Original files and folder choices are kept."))
            await ExecuteAsync(() => model.ResetIndexAsync(rebuild: false));
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        acting = true;
        Tabs.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        Feedback.Text = "";
        try { await action(); }
        catch (Exception error) { Feedback.Text = $"The change could not be completed. {error.Message}"; }
        finally
        {
            acting = false;
            Tabs.IsEnabled = true;
            ApplyButton.IsEnabled = true;
            UpdatePauseButton();
        }
    }
}

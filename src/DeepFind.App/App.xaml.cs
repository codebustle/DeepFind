using System.IO;
using System.Windows;
using DeepFind.Infrastructure;
using Lucene.Net.Index;
using Serilog;

namespace DeepFind.App;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 1 && DesktopShortcut.IsInstallerCommand(e.Args[0]))
        {
            Shutdown(DesktopShortcut.Run(e.Args[0]));
            return;
        }
        instance = new Mutex(true, @"Local\DeepFind.Desktop", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("DeepFind is already running.", "DeepFind");
            Shutdown();
            return;
        }

        var store = new SettingsStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepFind"));
        try
        {
            Log.Logger = new LoggerConfiguration().WriteTo.File(
                Path.Combine(store.DataDirectory, "Logs", "app-.log"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 5).CreateLogger();
            var settings = store.Load();
            var directory = store.IndexDirectory(settings);
            MainViewModel model;
            try { model = new(store, settings, directory); }
            catch (Exception error) when (error is CorruptIndexException or IndexFormatTooOldException or IndexFormatTooNewException)
            {
                var answer = MessageBox.Show("The search index cannot be read. Remove the damaged search index and rebuild from your selected folders?",
                    "DeepFind", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    Shutdown(1);
                    return;
                }
                var search = Path.Combine(directory, "Search");
                if (Directory.Exists(search))
                    Directory.Delete(search, recursive: true);
                model = new(store, settings with { Paused = false }, directory);
            }
            MainWindow = new MainWindow(model);
            MainWindow.Show();
        }
        catch (Exception error)
        {
            Log.Error("Startup failed with {ErrorType}", error.GetType().Name);
            MessageBox.Show($"DeepFind could not open its settings or local index.\n\n{error.Message}",
                "DeepFind", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        instance?.Dispose();
        base.OnExit(e);
    }
}

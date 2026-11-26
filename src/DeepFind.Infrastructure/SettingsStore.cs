using System.Text.Json;
using DeepFind.Core;

namespace DeepFind.Infrastructure;

public sealed class SettingsStore(string dataDirectory)
{
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    private string FilePath => Path.Combine(DataDirectory, "settings.json");

    public Settings Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new()
        : new();

    public void Save(Settings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = FilePath + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);
        }
        // A reader (including antivirus/indexers) may briefly hold the old file
        // without delete sharing on Windows. Preserve the old settings and retry
        // the atomic replacement; persistent permission/storage errors still surface.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, FilePath, overwrite: true);
                break;
            }
            catch (Exception error) when (attempt < 4 && (error is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    public string IndexDirectory(Settings settings) => string.IsNullOrWhiteSpace(settings.IndexLocation)
        ? Path.Combine(DataDirectory, "Index")
        : Path.Combine(Path.GetFullPath(settings.IndexLocation), "DeepFind.Index");
}

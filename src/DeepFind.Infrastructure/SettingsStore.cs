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
        File.Move(temporary, FilePath, overwrite: true);
    }

    public string IndexDirectory(Settings settings) => string.IsNullOrWhiteSpace(settings.IndexLocation)
        ? Path.Combine(DataDirectory, "Index")
        : Path.Combine(Path.GetFullPath(settings.IndexLocation), "DeepFind.Index");
}

using DeepFind.Core;
using Microsoft.Data.Sqlite;

namespace DeepFind.Infrastructure;

public sealed class TrackingStore
{
    private readonly string connectionString;

    public TrackingStore(string indexDirectory)
    {
        Directory.CreateDirectory(indexDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(indexDirectory, "tracking.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var database = Connect();
        using var command = database.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS files (
                id TEXT PRIMARY KEY, path TEXT NOT NULL, size INTEGER NOT NULL,
                modified INTEGER NOT NULL, issue TEXT, retry INTEGER NOT NULL, unavailable INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS scan_jobs (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                requested INTEGER NOT NULL, completed INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO scan_jobs VALUES (1, 0, 0);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, FileState> Load()
    {
        using var database = Connect();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT id, path, size, modified, issue, retry, unavailable FROM files";
        using var reader = command.ExecuteReader();
        var states = new Dictionary<string, FileState>(StringComparer.Ordinal);
        while (reader.Read())
            states.Add(reader.GetString(0), new(new(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6)));
        return states;
    }

    public bool HasPendingScan()
    {
        using var database = Connect();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT requested > completed FROM scan_jobs WHERE id = 1";
        return (long)command.ExecuteScalar()! != 0;
    }

    public long RequestScan()
    {
        using var database = Connect();
        using var command = database.CreateCommand();
        command.CommandText = "UPDATE scan_jobs SET requested = requested + 1 WHERE id = 1 RETURNING requested";
        return (long)command.ExecuteScalar()!;
    }

    public void CompleteScan(long generation)
    {
        using var database = Connect();
        using var command = database.CreateCommand();
        command.CommandText = "UPDATE scan_jobs SET completed = max(completed, $generation) WHERE id = 1";
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    public void Save(IEnumerable<FileState> updates, IEnumerable<string> removals)
    {
        using var database = Connect();
        using var transaction = database.BeginTransaction();
        using var upsert = database.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO files VALUES ($id, $path, $size, $modified, $issue, $retry, $unavailable)
            ON CONFLICT(id) DO UPDATE SET path=excluded.path, size=excluded.size, modified=excluded.modified,
                issue=excluded.issue, retry=excluded.retry, unavailable=excluded.unavailable
            """;
        foreach (var name in new[] { "$id", "$path", "$size", "$modified", "$issue", "$retry", "$unavailable" })
            upsert.Parameters.Add(new SqliteParameter { ParameterName = name });
        foreach (var state in updates)
        {
            var file = state.File;
            upsert.Parameters["$id"].Value = file.Id;
            upsert.Parameters["$path"].Value = file.Path;
            upsert.Parameters["$size"].Value = file.Size;
            upsert.Parameters["$modified"].Value = file.ModifiedTicks;
            upsert.Parameters["$issue"].Value = (object?)state.Issue ?? DBNull.Value;
            upsert.Parameters["$retry"].Value = state.Retry;
            upsert.Parameters["$unavailable"].Value = state.Unavailable;
            upsert.ExecuteNonQuery();
        }
        using var delete = database.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM files WHERE id = $id";
        var id = delete.Parameters.Add("$id", SqliteType.Text);
        foreach (var removal in removals)
        {
            id.Value = removal;
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Connect()
    {
        var database = new SqliteConnection(connectionString);
        database.Open();
        return database;
    }
}

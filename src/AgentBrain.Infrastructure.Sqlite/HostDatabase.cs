using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AgentBrain.Infrastructure.Sqlite;

/// <summary>
/// The authoritative server database (<c>/data/db/brain.db</c>): SQLite-stored entries and API key hashes.
/// Unlike the search index it is never rebuilt: schema changes are explicit, numbered upgrades, each
/// preceded by a backup (plan §9.6). One connection, used by one operation at a time.
/// </summary>
public sealed class HostDatabase : IDisposable
{
    public const int CurrentVersion = 1;

    private static readonly string[] Upgrades =
    [
        // Version 1.
        """
        CREATE TABLE store_entries(
            collection TEXT NOT NULL, id TEXT NOT NULL, metadata TEXT NOT NULL, body TEXT NOT NULL,
            revision TEXT NOT NULL, updated INTEGER NOT NULL, PRIMARY KEY (collection, id));
        CREATE TABLE api_keys(
            key_id TEXT PRIMARY KEY, secret_hash TEXT NOT NULL, scopes TEXT NOT NULL, label TEXT NOT NULL,
            created TEXT NOT NULL, revoked TEXT);
        """,
    ];

    private HostDatabase(SqliteConnection connection, string path)
    {
        Connection = connection;
        Path = path;
    }

    public SqliteConnection Connection { get; }

    public string Path { get; }

    /// <summary>Serialises access: Microsoft.Data.Sqlite connections are not thread-safe.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Opens the database, upgrading it after writing <c>pre-upgrade-v{n}.db</c> into <paramref name="backupDirectory"/>.</summary>
    public static HostDatabase Open(string path, string? backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path != ":memory:")
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        var database = new HostDatabase(connection, path);
        database.Execute("PRAGMA busy_timeout = 5000;");
        if (path != ":memory:")
        {
            database.Execute("PRAGMA journal_mode = WAL;");
        }

        database.Upgrade(backupDirectory);
        return database;
    }

    public int Version => (int)(long)Scalar("PRAGMA user_version")!;

    /// <summary>Copies the live database to <paramref name="destination"/> with SQLite's online backup.</summary>
    public void Backup(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        Connection.BackupDatabase(target);
    }

    public void Dispose()
    {
        Connection.Dispose();
        Gate.Dispose();
    }

    internal void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    internal object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    internal SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private void Upgrade(string? backupDirectory)
    {
        var version = Version;
        if (version > CurrentVersion)
        {
            throw new InvalidOperationException($"The database is version {version}; this server supports up to {CurrentVersion}. Upgrade brain-server.");
        }

        if (version > 0 && version < CurrentVersion && backupDirectory is not null && Path != ":memory:")
        {
            Backup(System.IO.Path.Combine(backupDirectory, $"pre-upgrade-v{version}-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.db"));
        }

        for (var next = version + 1; next <= CurrentVersion; next++)
        {
            using var transaction = Connection.BeginTransaction();
            Execute(Upgrades[next - 1]);
            Execute($"PRAGMA user_version = {next.ToString(CultureInfo.InvariantCulture)}");
            transaction.Commit();
        }
    }
}

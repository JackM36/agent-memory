using System.Globalization;
using AgentBrain.Application.Authorization;

namespace AgentBrain.Infrastructure.Sqlite;

/// <summary>API key records in the server database. Only secret hashes are stored.</summary>
public sealed class SqliteApiKeyStore(HostDatabase database) : IApiKeyStore
{
    public ValueTask AddAsync(ApiKeyRecord key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        database.Execute(
            "INSERT INTO api_keys(key_id, secret_hash, scopes, label, created, revoked) VALUES ($k, $h, $s, $l, $c, NULL)",
            ("$k", key.KeyId), ("$h", key.SecretHash), ("$s", string.Join('\n', key.Scopes)), ("$l", key.Label), ("$c", key.Created.ToString("O", CultureInfo.InvariantCulture)));
        return ValueTask.CompletedTask;
    }

    public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
    {
        using var command = database.Command("SELECT key_id, secret_hash, scopes, label, created, revoked FROM api_keys WHERE key_id = $k", ("$k", keyId));
        using var reader = command.ExecuteReader();
        return ValueTask.FromResult(reader.Read() ? ReadRecord(reader) : null);
    }

    public ValueTask<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        using var command = database.Command("SELECT key_id, secret_hash, scopes, label, created, revoked FROM api_keys ORDER BY created");
        using var reader = command.ExecuteReader();
        var keys = new List<ApiKeyRecord>();
        while (reader.Read())
        {
            keys.Add(ReadRecord(reader));
        }

        return ValueTask.FromResult<IReadOnlyList<ApiKeyRecord>>(keys);
    }

    public ValueTask<bool> RevokeAsync(string keyId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        using var command = database.Command("UPDATE api_keys SET revoked = $r WHERE key_id = $k AND revoked IS NULL", ("$r", at.ToString("O", CultureInfo.InvariantCulture)), ("$k", keyId));
        return ValueTask.FromResult(command.ExecuteNonQuery() == 1);
    }

    private static ApiKeyRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}

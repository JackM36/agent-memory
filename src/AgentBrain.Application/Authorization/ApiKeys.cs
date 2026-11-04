using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using AgentBrain.Application.Common;
using AgentBrain.Domain.Access;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Authorization;

/// <summary>A stored API key: only the SHA-256 of its secret is kept (spec §11, plan §10.3).</summary>
public sealed record ApiKeyRecord(string KeyId, string SecretHash, IReadOnlyList<string> Scopes, string Label, DateTimeOffset Created, DateTimeOffset? Revoked)
{
    public bool IsActive => Revoked is null;
}

public interface IApiKeyStore
{
    ValueTask AddAsync(ApiKeyRecord key, CancellationToken cancellationToken);

    ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken cancellationToken);

    ValueTask<bool> RevokeAsync(string keyId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>
/// Scoped API keys. Format <c>brain_&lt;keyId&gt;_&lt;secret&gt;</c>: the key ID locates the record, the secret
/// (32 random bytes, base32) is compared by hash in constant time. Scopes:
/// <c>collection:&lt;id|*&gt;:read|propose|maintain|publish</c> and <c>admin</c>.
/// </summary>
public sealed class ApiKeyService(IApiKeyStore store, TimeProvider time)
{
    public const string Prefix = "brain_";
    public const string AdminScope = "admin";

    public async ValueTask<Result<(ApiKeyRecord Record, string Key)>> CreateAsync(string label, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        foreach (var scope in scopes)
        {
            if (!IsValidScope(scope))
            {
                return Error.Validation($"'{scope}' is not a scope. Use collection:<id|*>:read|propose|maintain|publish or admin.");
            }
        }

        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var secret = Base32(RandomNumberGenerator.GetBytes(32));
        var record = new ApiKeyRecord(keyId, Hash(secret), scopes, label, time.GetUtcNow(), null);
        await store.AddAsync(record, cancellationToken);
        return (record, $"{Prefix}{keyId}_{secret}");
    }

    /// <summary>The active key record for a presented token, or null. Never throws on malformed input.</summary>
    public async ValueTask<ApiKeyRecord?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        if (!TryParse(token, out var keyId, out var secret))
        {
            return null;
        }

        var record = await store.FindAsync(keyId, cancellationToken);
        if (record is null || !record.IsActive)
        {
            return null;
        }

        var presented = Encoding.ASCII.GetBytes(Hash(secret));
        var stored = Encoding.ASCII.GetBytes(record.SecretHash);
        return CryptographicOperations.FixedTimeEquals(presented, stored) ? record : null;
    }

    /// <summary>Capabilities a key's scopes grant on a collection.</summary>
    public static Func<CollectionId, Capability> Grants(IReadOnlyList<string> scopes) => collection =>
    {
        var granted = Capability.None;
        foreach (var scope in scopes)
        {
            var parts = scope.Split(':');
            if (parts is ["collection", var id, var capability] && (id == "*" || id == collection.Value)
                && Capabilities.Names.TryParse(capability, out var c))
            {
                granted |= c;
            }
        }

        return granted;
    };

    public static bool IsAdmin(IReadOnlyList<string> scopes) => scopes.Contains(AdminScope);

    public static bool IsValidScope(string scope)
    {
        if (scope == AdminScope)
        {
            return true;
        }

        var parts = scope.Split(':');
        return parts is ["collection", var id, var capability]
            && (id == "*" || CollectionId.IsValid(id))
            && Capabilities.Names.TryParse(capability, out _);
    }

    private static bool TryParse(string? token, [NotNullWhen(true)] out string? keyId, [NotNullWhen(true)] out string? secret)
    {
        keyId = secret = null;
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length > 200)
        {
            return false;
        }

        var rest = token[Prefix.Length..];
        var separator = rest.IndexOf('_', StringComparison.Ordinal);
        if (separator <= 0 || separator == rest.Length - 1)
        {
            return false;
        }

        keyId = rest[..separator];
        secret = rest[(separator + 1)..];
        return true;
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var builder = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}

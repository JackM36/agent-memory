using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Configuration;

/// <summary>How <c>brain-server</c> receives connections (plan §9.6).</summary>
public enum HostTransport
{
    /// <summary>Kestrel terminates HTTPS with a configured certificate.</summary>
    Tls,

    /// <summary>Plain HTTP on a private network behind a TLS-terminating proxy (the Docker Compose default).</summary>
    BehindProxy,

    /// <summary>Loopback only.</summary>
    Local,
}

public enum HostStorage
{
    Sqlite,
    Markdown,
}

/// <summary>A collection the server hosts. SQLite collections take their descriptor from here; Markdown ones from their collection.json.</summary>
public sealed record HostedCollection(
    CollectionId Id,
    HostStorage Storage,
    string? Path,
    string Title,
    ApplicabilityOverrides Defaults,
    ProvenanceSource Source,
    bool ReadOnly,
    bool ImmediatePublish);

/// <summary>Server deployment configuration (<c>/data/host.json</c>). Not project configuration: no layering.</summary>
public sealed record HostConfiguration(
    HostTransport Transport,
    string Listen,
    string? CertificatePath,
    string? CertificatePasswordEnv,
    IReadOnlyList<string> TrustedProxies,
    IReadOnlyList<HostedCollection> Collections,
    int MaxResults,
    int MaxBytes,
    bool AllowInsecureHttp)
{
    public const int DefaultMaxResults = 100;
    public const int DefaultMaxBytes = 1_000_000;
}

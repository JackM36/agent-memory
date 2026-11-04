using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Common;
using AgentBrain.Application.Configuration;
using AgentBrain.Application.Retrieval;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Abstractions.Support;

/// <summary>An append-only record of a proposal or publishing action (spec §10.6). Not a content snapshot.</summary>
public sealed record AuditRecord(DateTimeOffset At, Attribution Actor, string Action, EntryRef? Target, string Outcome, string? Detail = null);

public interface IAuditLog
{
    ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken);
}

/// <summary>Loads the configuration documents that apply to one invocation (user, project, project-local).</summary>
public interface IConfigurationSource
{
    ValueTask<Result<ConfigurationInput>> LoadAsync(ConfigurationRequest request, CancellationToken cancellationToken);
}

/// <summary>A reference to a credential, never the credential itself (spec §11).</summary>
public sealed record CredentialReference(string EnvironmentVariable);

public interface ICredentialResolver
{
    /// <summary>The secret, or null when it is not available. Callers must never log or return it.</summary>
    string? Resolve(CredentialReference reference);
}

/// <summary>Serialises writers across processes (plan §5.12). Disposing the lease releases the lock.</summary>
public interface IWriteCoordinator
{
    ValueTask<Result<IAsyncDisposable>> AcquireAsync(string lockName, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>A get of one target that failed, reported alongside the targets that succeeded.</summary>
public sealed record GetFailure(EntryRef Target, Error Error);

/// <summary>What `brain index` reports.</summary>
public sealed record IndexReport(
    Abstractions.Indexing.IndexStatus Status,
    AgentBrain.Application.Indexing.SyncReport? Sync,
    IReadOnlyList<Abstractions.Indexing.IndexDiagnostic> Diagnostics);

/// <summary>
/// Renders responses in one output format. The budgeter measures the rendered UTF-8 size, so budgets
/// hold for the exact bytes the caller receives (plan §6.1, stage 8).
/// </summary>
public interface IResponseRenderer
{
    string Format { get; }

    string RenderSearch(SearchResponse response);

    string RenderList(ListResponse response);

    string RenderEntries(IReadOnlyList<EntryContent> entries, IReadOnlyList<GetFailure> failures);

    string RenderReferences(EntryRef source, IReadOnlyList<OutgoingReference> references);

    string RenderBacklinks(EntryRef target, Page<Backlink> backlinks);

    string RenderCollections(IReadOnlyList<CollectionInfo> collections);

    string RenderIndex(IndexReport report);

    string RenderError(Error error);
}

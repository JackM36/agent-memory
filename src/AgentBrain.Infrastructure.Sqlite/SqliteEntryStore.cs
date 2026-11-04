using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Common;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.Revisions;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Infrastructure.Sqlite;

/// <summary>
/// A collection stored in the authoritative server database (plan §9.6). Same semantics as a Markdown
/// collection — revision-checked writes, the same validation — plus atomic batches in one transaction.
/// It feeds the search index through <see cref="ICollectionScanner"/>, with the entry ID as the path and the
/// revision as the hash.
/// </summary>
public sealed class SqliteEntryStore(HostDatabase database, CollectionDescriptor collection, IContentAnalyzer analyzer, bool readOnly = false)
    : IEntryReader, IEntryWriter, ITransactionalEntryWriter, ICollectionScanner
{
    private const string Suffix = ".md";

    public CollectionDescriptor Collection { get; } = collection;

    public string Location => $"sqlite:{database.Path}#{Collection.Id}";

    public bool IsWritable => !readOnly && !Collection.ReadOnly;

    public IReadOnlyList<FileStamp> Scan()
    {
        using var command = database.Command("SELECT id, length(body), updated FROM store_entries WHERE collection = $c ORDER BY id", ("$c", Collection.Id.Value));
        using var reader = command.ExecuteReader();
        var stamps = new List<FileStamp>();
        while (reader.Read())
        {
            stamps.Add(new FileStamp(reader.GetString(0) + Suffix, reader.GetInt64(1), reader.GetInt64(2)));
        }

        return stamps;
    }

    public ScannedFile Load(FileStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var id = stamp.Path[..^Suffix.Length];
        var (entry, issues) = Read(id);
        return new ScannedFile(stamp, entry?.Revision.Value ?? "", entry, entry is null ? null : analyzer.Analyze(entry.Body), issues);
    }

    public ValueTask<StoredEntry?> FindAsync(EntryId id, string? locatorHint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ValueTask.FromResult(Read(id.Value).Entry);
    }

    public ValueTask<Result<Revision>> CreateAsync(EntryMetadata metadata, string body, CancellationToken cancellationToken) =>
        ValueTask.FromResult(InTransaction(() => Apply(new PreparedChange(ChangeKind.Create, new EntryRef(Collection.Id, metadata.Id), null, metadata, body))));

    public ValueTask<Result<Revision>> ReplaceAsync(EntryId id, Revision baseRevision, EntryMetadata metadata, string body, CancellationToken cancellationToken) =>
        ValueTask.FromResult(InTransaction(() => Apply(new PreparedChange(ChangeKind.Update, new EntryRef(Collection.Id, id), baseRevision, metadata, body))));

    public ValueTask<Result> DeleteAsync(EntryId id, Revision baseRevision, CancellationToken cancellationToken)
    {
        var result = InTransaction(() => Apply(new PreparedChange(ChangeKind.Delete, new EntryRef(Collection.Id, id), baseRevision, null, null)));
        return ValueTask.FromResult(result.IsSuccess ? Result.Success : Result.Failure(result.Error));
    }

    /// <summary>All changes commit together or none do (spec §10.5).</summary>
    public ValueTask<Result<IReadOnlyList<Revision?>>> ApplyAllAsync(IReadOnlyList<PreparedChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!IsWritable)
        {
            return ValueTask.FromResult<Result<IReadOnlyList<Revision?>>>(ReadOnlyError());
        }

        using var transaction = database.Connection.BeginTransaction();
        var revisions = new List<Revision?>();
        foreach (var change in changes)
        {
            var applied = Apply(change);
            if (!applied.IsSuccess)
            {
                transaction.Rollback();
                return ValueTask.FromResult<Result<IReadOnlyList<Revision?>>>(applied.Error with { Message = applied.Error.Message + " No change in the batch was applied." });
            }

            revisions.Add(change.Kind == ChangeKind.Delete ? null : applied.Value);
        }

        transaction.Commit();
        return ValueTask.FromResult<Result<IReadOnlyList<Revision?>>>(revisions);
    }

    private Result<Revision> InTransaction(Func<Result<Revision>> write)
    {
        if (!IsWritable)
        {
            return ReadOnlyError();
        }

        using var transaction = database.Connection.BeginTransaction();
        var result = write();
        if (result.IsSuccess)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
        }

        return result;
    }

    /// <summary>Applies one change inside the caller's transaction, checking the base revision first.</summary>
    private Result<Revision> Apply(PreparedChange change)
    {
        var id = change.Target.Entry.Value;
        var current = database.Scalar("SELECT revision FROM store_entries WHERE collection = $c AND id = $i", ("$c", Collection.Id.Value), ("$i", id)) as string;
        switch (change.Kind)
        {
            case ChangeKind.Create when current is not null:
                return Error.Validation($"'{change.Target}' already exists.");
            case ChangeKind.Update or ChangeKind.Delete when current is null:
                return Error.NotFound($"'{change.Target}' does not exist.");
            case ChangeKind.Update or ChangeKind.Delete when current != change.BaseRevision?.Value:
                return new Error(ErrorCode.RevisionConflict,
                    $"'{change.Target}' changed since it was read (current revision {current}). Fetch it again and re-apply the change.",
                    new Dictionary<string, string> { ["currentRevision"] = current! });
        }

        if (change.Kind == ChangeKind.Delete)
        {
            database.Execute("DELETE FROM store_entries WHERE collection = $c AND id = $i", ("$c", Collection.Id.Value), ("$i", id));
            return change.BaseRevision!;
        }

        var revision = RevisionCalculator.Compute(change.Metadata!, change.Body!);
        database.Execute(
            "INSERT INTO store_entries(collection, id, metadata, body, revision, updated) VALUES ($c, $i, $m, $b, $r, $u) " +
            "ON CONFLICT(collection, id) DO UPDATE SET metadata = excluded.metadata, body = excluded.body, revision = excluded.revision, updated = excluded.updated",
            ("$c", Collection.Id.Value), ("$i", id), ("$m", MetadataJson.Serialize(EntryMetadataWriter.ToNode(change.Metadata!))),
            ("$b", RevisionCalculator.NormalizeBody(change.Body!)), ("$r", revision.Value), ("$u", NextStamp(current)));
        return revision;
    }

    private (StoredEntry? Entry, IReadOnlyList<ValidationIssue> Issues) Read(string id)
    {
        using var command = database.Command("SELECT metadata, body FROM store_entries WHERE collection = $c AND id = $i", ("$c", Collection.Id.Value), ("$i", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (null, []);
        }

        var read = EntryMetadataReader.Read((MetadataMapping)MetadataJson.Deserialize(reader.GetString(0)));
        if (read.Metadata is null)
        {
            return (null, read.Issues);
        }

        var body = reader.GetString(1);
        var validation = EntryValidator.Validate(read.Metadata, Collection);
        return validation.IsValid
            ? (new StoredEntry(Collection.Id, read.Metadata, validation.Applicability!, body, RevisionCalculator.Compute(read.Metadata, body), id + Suffix), validation.Issues)
            : (null, validation.Issues);
    }

    /// <summary>Strictly increasing per entry, so the index sees every change even within one clock tick.</summary>
    private long NextStamp(string? current)
    {
        var now = DateTime.UtcNow.Ticks;
        if (current is null)
        {
            return now;
        }

        var previous = (long)(database.Scalar("SELECT updated FROM store_entries WHERE collection = $c AND revision = $r", ("$c", Collection.Id.Value), ("$r", current)) ?? 0L);
        return Math.Max(now, previous + 1);
    }

    private Error ReadOnlyError() => new(ErrorCode.ReadOnly, $"Collection '{Collection.Id}' is read-only.");
}

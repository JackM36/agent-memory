using AgentBrain.Application.Capture;
using AgentBrain.Application.Common;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Revisions;
using AgentBrain.Domain.Validation;

namespace AgentBrain.Application.Writing;

/// <summary>
/// Turns a caller's request into a <see cref="PreparedChange"/>: generates IDs, normalises values, applies
/// patches, stamps dates, validates against the collection and scans for secrets (plan §8.3). Shared by
/// direct maintenance, proposals and publishing, so every write path enforces the same rules.
/// </summary>
public sealed class ChangePreparer(TimeProvider time)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    public async ValueTask<Result<PreparedChange>> CreateAsync(WritableCollection collection, NewEntry entry, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(entry);
        var id = entry.Id ?? EntryId.FromTitle(entry.Title);
        if (entry.Id is null)
        {
            for (var n = 2; await collection.Reader.FindAsync(id, null, cancellationToken) is not null; n++)
            {
                id = EntryId.FromTitle(entry.Title).WithCollisionSuffix(n);
            }
        }
        else if (await collection.Reader.FindAsync(id, null, cancellationToken) is not null)
        {
            return Error.Validation($"'{collection.Info.Id}/{id}' already exists; update it instead.");
        }

        var metadata = new EntryMetadata
        {
            Id = id,
            Kind = entry.Kind,
            Title = entry.Title.Trim(),
            Summary = entry.Summary.Trim(),
            Tags = NormalizeTags(entry.Tags),
            Files = [.. entry.Files.Select(f => f.Trim()).Where(f => f.Length > 0)],
            Symbols = [.. entry.Symbols.Select(s => s.Trim()).Where(s => s.Length > 0)],
            Evidence = [.. entry.Evidence.Select(e => e.Trim()).Where(e => e.Length > 0)],
            Applies = entry.Applies,
            Source = entry.Source,
            Resolution = entry.Kind == EntryKind.Uncertainty ? Resolution.Unresolved : null,
            Blocking = entry.Kind == EntryKind.Uncertainty && entry.Blocking ? true : null,
            Created = Today,
            Updated = Today,
        };

        return Finish(collection, new PreparedChange(ChangeKind.Create, new EntryRef(collection.Info.Id, id), null, metadata, Body(entry.Body)), allowedSecretRules);
    }

    public async ValueTask<Result<PreparedChange>> UpdateAsync(
        WritableCollection collection, EntryId id, Revision baseRevision, EntryPatch patch, IReadOnlyCollection<string> allowedSecretRules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(patch);
        var current = await collection.Reader.FindAsync(id, null, cancellationToken);
        if (current is null)
        {
            return Error.NotFound($"'{collection.Info.Id}/{id}' does not exist.");
        }

        if (current.Revision != baseRevision)
        {
            return Conflict(collection, id, current.Revision);
        }

        var m = current.Metadata;
        var resolving = patch.ResolvedBy is not null;
        var metadata = m with
        {
            Title = patch.Title?.Trim() ?? m.Title,
            Summary = patch.Summary?.Trim() ?? m.Summary,
            Tags = patch.Tags is null ? m.Tags : NormalizeTags(patch.Tags),
            Files = patch.Files ?? m.Files,
            Symbols = patch.Symbols ?? m.Symbols,
            Evidence = patch.Evidence ?? m.Evidence,
            Verified = patch.Verified ?? m.Verified,
            Archived = patch.Archive ? m.Archived ?? Today : m.Archived,
            SupersededBy = patch.SupersededBy ?? m.SupersededBy,
            Resolution = resolving ? Resolution.Resolved : m.Resolution,
            ResolvedBy = patch.ResolvedBy ?? m.ResolvedBy,
            Blocking = resolving ? null : patch.Blocking ?? m.Blocking,
            Publication = patch.Publication ?? m.Publication,
            Updated = Today,
        };

        var body = patch.Body is null ? current.Body : Body(patch.Body);
        return Finish(collection, new PreparedChange(ChangeKind.Update, new EntryRef(collection.Info.Id, id), baseRevision, metadata, body), allowedSecretRules);
    }

    public static async ValueTask<Result<PreparedChange>> DeleteAsync(WritableCollection collection, EntryId id, Revision baseRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var current = await collection.Reader.FindAsync(id, null, cancellationToken);
        if (current is null)
        {
            return Error.NotFound($"'{collection.Info.Id}/{id}' does not exist.");
        }

        return current.Revision != baseRevision
            ? Conflict(collection, id, current.Revision)
            : new PreparedChange(ChangeKind.Delete, new EntryRef(collection.Info.Id, id), baseRevision, null, null);
    }

    /// <summary>Validates and secret-scans a prepared change (also used when re-checking stored proposals).</summary>
    public static Result<PreparedChange> Finish(WritableCollection collection, PreparedChange change, IReadOnlyCollection<string> allowedSecretRules)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(change);
        if (change.Metadata is null || change.Body is null)
        {
            return change;
        }

        var validation = EntryValidator.Validate(change.Metadata, collection.Reader.Collection);
        var errors = validation.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            return Error.Validation(
                "The entry is not valid: " + string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}")),
                errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.First().Message));
        }

        var secrets = SecretScanner.Scan(change.Metadata, change.Body, allowedSecretRules);
        if (secrets.Count > 0)
        {
            return new Error(
                ErrorCode.SecretDetected,
                "Possible secret found: " + string.Join("; ", secrets.Select(s => $"{s.Rule} in {s.Field} line {s.Line}")) +
                ". Remove it, or allow one rule with --allow-secret-rule only after the user confirms it is a false positive.",
                secrets.GroupBy(s => s.Rule).ToDictionary(g => g.Key, g => $"{g.First().Field}:{g.First().Line}"));
        }

        return change;
    }

    private static Error Conflict(WritableCollection collection, EntryId id, Revision current) => new(
        ErrorCode.RevisionConflict,
        $"'{collection.Info.Id}/{id}' changed since it was read (current revision {current}). Fetch it again and re-apply the change.",
        new Dictionary<string, string> { ["currentRevision"] = current.Value });

    private static List<string> NormalizeTags(IEnumerable<string> tags) =>
        [.. tags.Select(Names.NormalizeTag).OfType<string>().Distinct(StringComparer.Ordinal)];

    private static string Body(string body)
    {
        var normalized = body.ReplaceLineEndings("\n").Trim('\n');
        return normalized.Length == 0 ? "" : normalized + "\n";
    }
}

using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Support;

namespace AgentBrain.Application.Indexing;

public enum SyncMode
{
    /// <summary>Compare size and modification time; hash and reparse only changed files.</summary>
    Incremental,

    /// <summary>Hash every file to catch changes metadata checks missed.</summary>
    Verify,

    /// <summary>Discard derived state and rebuild from authoritative content.</summary>
    Rebuild,
}

/// <summary>Outcome of a synchronisation. <see cref="Skipped"/> means another process held the index lock.</summary>
public sealed record SyncReport(int Added, int Updated, int Removed, int Unchanged, bool Skipped)
{
    public static readonly SyncReport None = new(0, 0, 0, 0, Skipped: true);

    public bool Changed => Added + Updated + Removed > 0;
}

/// <summary>
/// Brings the index in line with file-backed collections (plan §5.14). Refresh is serialised across
/// processes by an index lock; a caller that cannot get it quickly searches the existing index and
/// reports it as stale instead of waiting.
/// </summary>
public sealed class IndexSynchronizer(ISearchIndex index, IReadOnlyList<ICollectionScanner> scanners, IWriteCoordinator locks)
{
    public const string LockName = "index";

    /// <summary>How long a search waits for another process's refresh before using the index as is.</summary>
    public static readonly TimeSpan SearchLockWait = TimeSpan.FromMilliseconds(750);

    /// <summary>Maintenance commands wait longer: they exist to refresh.</summary>
    public static readonly TimeSpan MaintenanceLockWait = TimeSpan.FromSeconds(30);

    public async ValueTask<SyncReport> SynchronizeAsync(SyncMode mode, TimeSpan lockWait, CancellationToken cancellationToken)
    {
        var lease = await locks.AcquireAsync(LockName, lockWait, cancellationToken);
        if (!lease.IsSuccess)
        {
            return SyncReport.None;
        }

        await using (lease.Value)
        {
            if (mode == SyncMode.Rebuild)
            {
                await index.ResetAsync(cancellationToken);
            }

            int added = 0, updated = 0, removed = 0, unchanged = 0;
            foreach (var scanner in scanners)
            {
                var collection = scanner.Collection.Id;
                var indexed = await index.GetStampsAsync(collection, cancellationToken);
                var upserts = new List<ScannedFile>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var stamp in scanner.Scan())
                {
                    seen.Add(stamp.Path);
                    var known = indexed.GetValueOrDefault(stamp.Path);
                    var stampChanged = known is null || known.Size != stamp.Size || known.ModifiedTicks != stamp.ModifiedTicks;
                    if (!stampChanged && mode == SyncMode.Incremental)
                    {
                        unchanged++;
                        continue;
                    }

                    var file = scanner.Load(stamp);
                    if (known is not null && known.Hash == file.Hash && !stampChanged)
                    {
                        unchanged++;
                        continue;
                    }

                    upserts.Add(file);
                    if (known is null)
                    {
                        added++;
                    }
                    else
                    {
                        updated++;
                    }
                }

                var gone = indexed.Keys.Where(path => !seen.Contains(path)).ToList();
                removed += gone.Count;
                if (upserts.Count > 0 || gone.Count > 0)
                {
                    await index.ApplyAsync(new IndexChanges(collection, upserts, gone), cancellationToken);
                }
            }

            await index.RetainCollectionsAsync([.. scanners.Select(s => s.Collection.Id)], cancellationToken);
            return new SyncReport(added, updated, removed, unchanged, Skipped: false);
        }
    }
}

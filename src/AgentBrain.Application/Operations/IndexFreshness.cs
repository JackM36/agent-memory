using AgentBrain.Application.Indexing;

namespace AgentBrain.Application.Operations;

/// <summary>
/// Refreshes the index before reads (plan §5.14): an incremental scan that waits briefly for another
/// process's refresh, then reports the index as stale rather than blocking.
/// </summary>
public sealed class IndexFreshness(IndexSynchronizer? synchronizer)
{
    /// <summary>Returns true when the index could not be refreshed and may be stale.</summary>
    public async ValueTask<bool> EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (synchronizer is null)
        {
            return false;
        }

        var report = await synchronizer.SynchronizeAsync(SyncMode.Incremental, IndexSynchronizer.SearchLockWait, cancellationToken);
        return report.Skipped;
    }
}

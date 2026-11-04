using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Application.Indexing;

namespace AgentBrain.Application.Operations.Index;

/// <summary>Index status, incremental refresh, full verification and rebuild (plan §5.14).</summary>
public sealed class IndexOperation(ISearchIndex? index, IndexSynchronizer? synchronizer)
{
    public async ValueTask<Result<IndexReport>> StatusAsync(CancellationToken cancellationToken)
    {
        if (index is null)
        {
            return new Error(ErrorCode.IndexUnavailable, "No local collections are configured, so there is no index.");
        }

        return new IndexReport(await index.GetStatusAsync(cancellationToken), null, await index.DiagnosticsAsync(cancellationToken));
    }

    public async ValueTask<Result<IndexReport>> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken)
    {
        if (index is null || synchronizer is null)
        {
            return new Error(ErrorCode.IndexUnavailable, "No local collections are configured, so there is no index.");
        }

        var sync = await synchronizer.SynchronizeAsync(mode, IndexSynchronizer.MaintenanceLockWait, cancellationToken);
        if (sync.Skipped)
        {
            return new Error(ErrorCode.LockTimeout, "Another process is refreshing the index; try again shortly.");
        }

        return new IndexReport(await index.GetStatusAsync(cancellationToken), sync, await index.DiagnosticsAsync(cancellationToken));
    }
}

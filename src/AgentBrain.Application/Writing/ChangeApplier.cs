using AgentBrain.Application.Common;
using AgentBrain.Domain.Revisions;

namespace AgentBrain.Application.Writing;

/// <summary>Writes a prepared change through the collection's storage. Storage re-checks the base revision under its own lock.</summary>
public static class ChangeApplier
{
    public static async ValueTask<Result<Revision?>> ApplyAsync(WritableCollection collection, PreparedChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(change);
        if (collection.Writer is null)
        {
            return new Error(ErrorCode.ReadOnly, $"Collection '{collection.Info.Id}' cannot be written by this process.");
        }

        switch (change.Kind)
        {
            case ChangeKind.Create:
            {
                var created = await collection.Writer.CreateAsync(change.Metadata!, change.Body!, cancellationToken);
                return created.IsSuccess ? created.Value : created.Error;
            }

            case ChangeKind.Update:
            {
                var replaced = await collection.Writer.ReplaceAsync(change.Target.Entry, change.BaseRevision!, change.Metadata!, change.Body!, cancellationToken);
                return replaced.IsSuccess ? replaced.Value : replaced.Error;
            }

            default:
            {
                var deleted = await collection.Writer.DeleteAsync(change.Target.Entry, change.BaseRevision!, cancellationToken);
                return deleted.IsSuccess ? Result<Revision?>.Success(null) : deleted.Error;
            }
        }
    }
}

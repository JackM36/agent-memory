using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;

namespace AgentBrain.Infrastructure.Local.Locking;

/// <summary>
/// Cross-process locks as exclusively opened files in <c>local/state/locks/</c> (plan §5.12). The
/// operating system releases the lock if the process dies, so a crash never leaves a stale lock behind.
/// It coordinates Agent Brain processes only; it cannot stop external editors.
/// </summary>
public sealed class FileWriteCoordinator(string lockDirectory, TimeProvider time) : IWriteCoordinator
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    public async ValueTask<Result<IAsyncDisposable>> AcquireAsync(string lockName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);
        Directory.CreateDirectory(lockDirectory);
        var path = Path.Combine(lockDirectory, lockName + ".lock");
        var deadline = time.GetUtcNow() + timeout;

        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                return new Lease(stream);
            }
            catch (IOException) when (time.GetUtcNow() < deadline)
            {
                await Task.Delay(RetryDelay, time, cancellationToken);
            }
            catch (IOException)
            {
                return new Error(ErrorCode.LockTimeout, $"Timed out waiting for the '{lockName}' lock held by another Agent Brain process.");
            }
            catch (UnauthorizedAccessException) when (time.GetUtcNow() < deadline)
            {
                // Windows reports a file pending deletion by another lock holder as access denied.
                await Task.Delay(RetryDelay, time, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                return new Error(ErrorCode.LockTimeout, $"Timed out waiting for the '{lockName}' lock held by another Agent Brain process.");
            }
        }
    }

    private sealed class Lease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

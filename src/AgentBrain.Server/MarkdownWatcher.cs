using AgentBrain.Application.Indexing;
using AgentBrain.Composition;

namespace AgentBrain.Server;

/// <summary>
/// Keeps the index fresh for a long-running host (spec §9.3): file notifications for hosted Markdown roots
/// trigger a debounced incremental sync, and a periodic reconciliation (plus one at startup) catches anything
/// the watchers missed. Watcher buffer overflow triggers an immediate reconciliation.
/// </summary>
internal sealed partial class MarkdownWatcher(HostRuntime runtime, WatcherOptions options, ILogger<MarkdownWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _changed = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchers = options.Notifications ? runtime.MarkdownRoots.Select(CreateWatcher).ToList() : [];
        try
        {
            await SyncAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var signalled = await _changed.WaitAsync(options.Reconciliation, stoppingToken);
                if (signalled)
                {
                    await Task.Delay(Debounce, stoppingToken);
                    while (_changed.CurrentCount > 0)
                    {
                        await _changed.WaitAsync(stoppingToken);
                    }
                }

                await SyncAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    public override void Dispose()
    {
        _changed.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "File watching failed for {Root}; reconciling now.")]
    private static partial void WatchFailed(ILogger logger, Exception exception, string root);

    [LoggerMessage(Level = LogLevel.Information, Message = "Index refreshed: {Added} added, {Updated} updated, {Removed} removed.")]
    private static partial void Refreshed(ILogger logger, int added, int updated, int removed);

    private FileSystemWatcher CreateWatcher(string root)
    {
        var watcher = new FileSystemWatcher(root, "*.md") { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
        FileSystemEventHandler signal = (_, _) => _changed.Release();
        watcher.Created += signal;
        watcher.Changed += signal;
        watcher.Deleted += signal;
        watcher.Renamed += (_, _) => _changed.Release();
        watcher.Error += (_, e) =>
        {
            WatchFailed(logger, e.GetException(), root);
            _changed.Release();
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        await runtime.Database.Gate.WaitAsync(cancellationToken);
        try
        {
            var report = await runtime.Synchronizer.SynchronizeAsync(SyncMode.Incremental, IndexSynchronizer.MaintenanceLockWait, cancellationToken);
            if (report.Changed)
            {
                Refreshed(logger, report.Added, report.Updated, report.Removed);
            }
        }
        finally
        {
            runtime.Database.Gate.Release();
        }
    }
}

/// <summary>How the host keeps hosted Markdown fresh. Tests turn notifications off to prove reconciliation alone recovers.</summary>
public sealed record WatcherOptions(TimeSpan Reconciliation, bool Notifications = true)
{
    public static readonly WatcherOptions Default = new(TimeSpan.FromMinutes(5));
}

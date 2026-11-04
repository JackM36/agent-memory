namespace AgentBrain.Server.Tests;

/// <summary>Spec §15.4: periodic reconciliation recovers changes the file watcher never reported.</summary>
public sealed class WatcherTests
{
    [Fact]
    public async Task Reconciliation_recovers_missed_notifications()
    {
        // Notifications off: only the reconciliation loop can notice the new file. The index is inspected
        // directly, because every API request also refreshes it.
        await using var host = await TestHost.StartAsync(watcher: new WatcherOptions(TimeSpan.FromMilliseconds(100), Notifications: false));
        var before = await EntriesAsync(host);

        await File.WriteAllTextAsync(Path.Combine(host.Data, "notes", "deploy.md"),
            "---\nschema: 1\nid: deploy\nkind: knowledge\ntitle: Deploy runbook\nsummary: How to deploy the ingestion service.\n---\nRun the pipeline.\n",
            TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await EntriesAsync(host) == before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(before + 1, await EntriesAsync(host));
    }

    private static async Task<int> EntriesAsync(TestHost host)
    {
        await host.Runtime.Database.Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            return (await host.Runtime.SearchIndex.GetStatusAsync(TestContext.Current.CancellationToken)).Entries;
        }
        finally
        {
            host.Runtime.Database.Gate.Release();
        }
    }
}

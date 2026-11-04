using System.Text.Json;
using AgentBrain.Cli.Infrastructure;


namespace AgentBrain.Cli.Tests;

/// <summary>End-to-end write flows: maintenance, conflicts, secrets, proposals, publishing and batches.</summary>
// Shares the process-wide AGENT_BRAIN_CONFIG_DIR with CommandLineTests, so the two never run in parallel.
[Collection("cli")]
public sealed class WriteCommandTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("brain-write-cli-").FullName;
    private readonly string _project;

    public WriteCommandTests()
    {
        _project = Path.Combine(_workspace, "game");
        var kb = Path.Combine(_project, ".agent-brain");
        Directory.CreateDirectory(Path.Combine(kb, "memory"));
        Directory.CreateDirectory(Path.Combine(kb, "curated"));
        Directory.CreateDirectory(Path.Combine(kb, "package"));
        File.WriteAllText(Path.Combine(kb, "memory", "collection.json"),
            """{ "schema": 1, "id": "game.memory", "title": "Game memory", "defaults": { "applies": { "scope": "project", "projects": ["game"] } } }""");
        File.WriteAllText(Path.Combine(kb, "curated", "collection.json"),
            """{ "schema": 1, "id": "team.kb", "title": "Team knowledge", "defaults": { "applies": { "scope": "general" }, "source": "curated" } }""");
        File.WriteAllText(Path.Combine(kb, "package", "collection.json"),
            """{ "schema": 1, "id": "vendor.kb", "title": "Vendor package", "readOnly": true, "defaults": { "applies": { "scope": "general" } } }""");
        File.WriteAllText(Path.Combine(kb, "package", "guide.md"),
            "---\nschema: 1\nid: guide\nkind: knowledge\ntitle: Vendor guide\nsummary: How to use the vendor package.\n---\nUse it carefully.\n");
        WriteConfig(immediatePublish: false);
        Directory.CreateDirectory(Path.Combine(_workspace, "user"));
        Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", Path.Combine(_workspace, "user"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AGENT_BRAIN_CONFIG_DIR", null);
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private void WriteConfig(bool immediatePublish) =>
        File.WriteAllText(Path.Combine(_project, ".agent-brain", "config.json"), $$"""
            {
              "schema": 1,
              "project": { "id": "game" },
              "collections": {
                "game.memory": { "path": "memory", "capabilities": ["read", "maintain"] },
                "team.kb": { "path": "curated", "capabilities": ["read", "propose", "publish"], "immediatePublish": {{(immediatePublish ? "true" : "false")}} },
                "vendor.kb": { "path": "package", "capabilities": ["read", "maintain", "publish"] }
              },
              "writes": { "defaultCollection": "game.memory" }
            }
            """);

    private async Task<(int Exit, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var io = new CliIo(output, TextWriter.Null, OutputIsTerminal: false, _project);
        var exit = await BrainCommandLine.Create(io).Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        return (exit, output.ToString());
    }

    private async Task<string> RevisionOf(string reference)
    {
        var (_, output) = await RunAsync("get", reference, "--format", "json");
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("entries")[0].GetProperty("entry").GetProperty("rev").GetString()!;
    }

    [Fact]
    public async Task Create_update_and_conflict_round_trip()
    {
        var (exit, created) = await RunAsync("new", "memory", "--title", "Save slots use atomic writes", "--summary", "Slots are written to a temp file and renamed.", "--tag", "Save System", "--body", "Details.");
        Assert.Equal(0, exit);
        Assert.StartsWith("created game.memory/save-slots-use-atomic-writes · rev r1-", created, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_project, ".agent-brain", "memory", "memory", "save-slots-use-atomic-writes.md")));

        var (_, search) = await RunAsync("search", "atomic save slots");
        Assert.Contains("game.memory/save-slots-use-atomic-writes", search, StringComparison.Ordinal);

        var revision = await RevisionOf("game.memory/save-slots-use-atomic-writes");
        var (updateExit, _) = await RunAsync("update", "game.memory/save-slots-use-atomic-writes", "--base-revision", revision, "--summary", "Slots are written atomically via rename.");
        Assert.Equal(0, updateExit);

        var (staleExit, stale) = await RunAsync("update", "game.memory/save-slots-use-atomic-writes", "--base-revision", revision, "--title", "Lost update");
        Assert.Equal(4, staleExit);
        Assert.StartsWith("error revision_conflict", stale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicates_are_reported_and_ids_get_suffixes()
    {
        await RunAsync("new", "memory", "--title", "Physics layers", "--summary", "Projectiles ignore the player layer.");
        var (_, second) = await RunAsync("new", "memory", "--title", "Physics layers", "--summary", "Projectiles ignore the player layer in the matrix.");

        Assert.Contains("created game.memory/physics-layers-2", second, StringComparison.Ordinal);
        Assert.Contains("similar existing entries", second, StringComparison.Ordinal);
        Assert.Contains("game.memory/physics-layers ", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secrets_block_writes_with_exit_8_unless_the_rule_is_allowed()
    {
        var (exit, output) = await RunAsync("new", "memory", "--title", "Deploy key", "--summary", "Where the key lives.", "--body", "key AKIAIOSFODNN7EXAMPLE");
        Assert.Equal(8, exit);
        Assert.Contains("aws-access-key in body line 1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", output, StringComparison.Ordinal);

        var (allowed, _) = await RunAsync("new", "memory", "--title", "Deploy key", "--summary", "Where the key lives.", "--body", "key AKIAIOSFODNN7EXAMPLE", "--allow-secret-rule", "aws-access-key");
        Assert.Equal(0, allowed);
    }

    [Fact]
    public async Task Lifecycle_commands_archive_supersede_and_resolve()
    {
        await RunAsync("new", "uncertainty", "--title", "Save format undecided", "--summary", "Ask before changing saves.", "--blocking");
        await RunAsync("new", "memory", "--title", "Save format decision", "--summary", "Old saves are migrated.");
        var (_, blocking) = await RunAsync("list", "--blocking");
        Assert.Contains("game.memory/save-format-undecided", blocking, StringComparison.Ordinal);

        var question = await RevisionOf("game.memory/save-format-undecided");
        var (resolved, _) = await RunAsync("resolve", "game.memory/save-format-undecided", "--by", "game.memory/save-format-decision", "--base-revision", question);
        Assert.Equal(0, resolved);
        var (_, afterResolve) = await RunAsync("list", "--blocking");
        Assert.DoesNotContain("save-format-undecided", afterResolve, StringComparison.Ordinal);

        var decision = await RevisionOf("game.memory/save-format-decision");
        var (archived, _) = await RunAsync("archive", "game.memory/save-format-decision", "--base-revision", decision);
        Assert.Equal(0, archived);
        var (_, search) = await RunAsync("search", "save format decision");
        Assert.DoesNotContain("game.memory/save-format-decision", search, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Curated_collections_refuse_direct_writes_and_take_proposals()
    {
        var (directExit, direct) = await RunAsync("new", "knowledge", "--collection", "team.kb", "--title", "Retry policy", "--summary", "Retry idempotent calls only.");
        Assert.Equal(5, directExit);
        Assert.Contains("Use brain propose", direct, StringComparison.Ordinal);

        var (proposeExit, proposed) = await RunAsync("propose", "create", "--collection", "team.kb", "--title", "Retry policy", "--summary", "Retry idempotent calls only.", "--note", "From incident review");
        Assert.Equal(0, proposeExit);
        var id = proposed.Split(' ')[0];
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_project, ".agent-brain", "curated"), "*.md", SearchOption.AllDirectories));

        var (acceptExit, _) = await RunAsync("proposals", "accept", id, "--note", "Looks right");
        Assert.Equal(0, acceptExit);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_project, ".agent-brain", "curated"), "retry-policy.md", SearchOption.AllDirectories));

        var (_, listed) = await RunAsync("proposals", "list", "--status", "accepted");
        Assert.Contains(id, listed, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_project, ".agent-brain", "local", "state", "audit"), "*.jsonl"));
    }

    [Fact]
    public async Task Read_only_packages_get_local_proposals_that_never_touch_them()
    {
        var revision = await RevisionOf("vendor.kb/guide");
        var before = File.ReadAllText(Path.Combine(_project, ".agent-brain", "package", "guide.md"));

        var (directExit, _) = await RunAsync("update", "vendor.kb/guide", "--base-revision", revision, "--summary", "Changed");
        Assert.Equal(5, directExit);

        var (proposeExit, proposed) = await RunAsync("propose", "update", "vendor.kb/guide", "--base-revision", revision, "--summary", "Clearer summary.");
        Assert.Equal(0, proposeExit);
        var id = proposed.Split(' ')[0];

        var (acceptExit, _) = await RunAsync("proposals", "accept", id);
        Assert.Equal(5, acceptExit);
        var (rejectExit, _) = await RunAsync("proposals", "reject", id, "--reason", "Report upstream instead");
        Assert.Equal(0, rejectExit);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_project, ".agent-brain", "package", "guide.md")));
    }

    [Fact]
    public async Task Publishing_is_disabled_by_default_and_batches_apply_together_when_enabled()
    {
        var (disabledExit, disabled) = await RunAsync("publish", "create", "--collection", "team.kb", "--title", "Naming", "--summary", "Name by intent.");
        Assert.Equal(5, disabledExit);
        Assert.Contains("Immediate publishing is disabled", disabled, StringComparison.Ordinal);

        WriteConfig(immediatePublish: true);
        var batch = Path.Combine(_workspace, "batch.json");
        File.WriteAllText(batch, """
            { "changes": [
              { "kind": "create", "collection": "team.kb", "entryKind": "knowledge", "title": "Naming", "summary": "Name by intent." },
              { "kind": "create", "collection": "team.kb", "entryKind": "knowledge", "title": "Guard clauses", "summary": "Return early." }
            ] }
            """);

        var (exit, output) = await RunAsync("publish", "--batch", batch);

        Assert.Equal(0, exit);
        Assert.Contains("published create team.kb/naming", output, StringComparison.Ordinal);
        Assert.Contains("published create team.kb/guard-clauses", output, StringComparison.Ordinal);
        var (_, status) = await RunAsync("batch", "status");
        Assert.Equal("no interrupted batches\n", status);
    }

    [Fact]
    public async Task Mixed_collection_batches_are_rejected()
    {
        WriteConfig(immediatePublish: true);
        var batch = Path.Combine(_workspace, "mixed.json");
        File.WriteAllText(batch, """
            { "changes": [
              { "kind": "create", "collection": "team.kb", "title": "A", "summary": "A." },
              { "kind": "create", "collection": "game.memory", "title": "B", "summary": "B." }
            ] }
            """);

        var (exit, output) = await RunAsync("publish", "--batch", batch);

        Assert.Equal(2, exit);
        Assert.Contains("one collection only", output, StringComparison.Ordinal);
    }
}

using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.Revisions;
using AgentBrain.Infrastructure.Markdown.Collections;
using AgentBrain.Infrastructure.Markdown.Content;
using AgentBrain.Infrastructure.Markdown.Frontmatter;
using AgentBrain.Infrastructure.Markdown.Writing;

namespace AgentBrain.Infrastructure.Markdown.Tests;

public sealed class WritingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("brain-write-").FullName;

    public void Dispose()
    {
        AtomicFileWriter.BeforeRename = null;
        Directory.Delete(_root, recursive: true);
    }

    private sealed class NoLocks : IWriteCoordinator
    {
        public ValueTask<Result<IAsyncDisposable>> AcquireAsync(string lockName, TimeSpan timeout, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Result<IAsyncDisposable>>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private MarkdownCollection Open(bool readOnly = false)
    {
        File.WriteAllText(Path.Combine(_root, "collection.json"),
            $$"""{ "schema": 1, "id": "kb", "title": "KB", "readOnly": {{(readOnly ? "true" : "false")}}, "defaults": { "applies": { "scope": "general" } } }""");
        return MarkdownCollection.Open(_root, new MarkdownAnalyzer(), new NoLocks()).Value;
    }

    private static EntryMetadata Metadata(string id = "tricky", string title = "Title: with \"quotes\" # and colon") => new()
    {
        Id = EntryId.Parse(id),
        Kind = EntryKind.Memory,
        Title = title,
        Summary = "null",
        Tags = ["a", "b-c"],
        Files = ["Assets/**/*.cs"],
        Applies = new ApplicabilityOverrides(Packages: [new PackageConstraint("unity", ">=2021.3 <2023")]),
        Evidence = ["user confirmed 2026-11-02", "line\nbreak"],
        Verified = new Verification(new DateOnly(2026, 11, 2), VerificationMethod.Code),
        SupersededBy = EntryRef.Parse("kb/other"),
        Created = new DateOnly(2026, 11, 1),
    };

    [Fact]
    public void Emitted_frontmatter_reads_back_to_the_same_metadata()
    {
        var metadata = Metadata();
        var text = FrontmatterEmitter.Write(EntryMetadataWriter.ToNode(metadata), "# Body\r\nText\n");

        var (document, error) = FrontmatterParser.Parse(text);
        Assert.Null(error);
        var reread = EntryMetadataReader.Read(document!.Metadata);
        Assert.True(reread.Metadata is not null, string.Join("; ", reread.Issues));
        Assert.Equal(RevisionCalculator.Compute(metadata, "# Body\nText\n"), RevisionCalculator.Compute(reread.Metadata, document.Body));
        Assert.Contains("tags: [a, b-c]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_replace_and_delete_check_revisions()
    {
        var collection = Open();
        var created = await collection.CreateAsync(Metadata("entry", "Plain title"), "Body\n", TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.True(File.Exists(Path.Combine(_root, "memory", "entry.md")));

        var duplicate = await collection.CreateAsync(Metadata("entry", "Plain title"), "Body\n", TestContext.Current.CancellationToken);
        Assert.False(duplicate.IsSuccess);

        var updated = Metadata("entry", "New title");
        var replaced = await collection.ReplaceAsync(EntryId.Parse("entry"), created.Value, updated, "Body\n", TestContext.Current.CancellationToken);
        Assert.True(replaced.IsSuccess);

        var stale = await collection.ReplaceAsync(EntryId.Parse("entry"), created.Value, updated, "Other\n", TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.RevisionConflict, stale.Error!.Code);
        Assert.Equal(replaced.Value.Value, stale.Error.Details!["currentRevision"]);

        Assert.True((await collection.DeleteAsync(EntryId.Parse("entry"), replaced.Value, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.False(File.Exists(Path.Combine(_root, "memory", "entry.md")));
    }

    [Fact]
    public async Task External_edits_are_detected_before_replacing()
    {
        var collection = Open();
        var created = await collection.CreateAsync(Metadata("entry", "Plain title"), "Body\n", TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, "memory", "entry.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Body", "Edited by hand", StringComparison.Ordinal));

        var result = await collection.ReplaceAsync(EntryId.Parse("entry"), created.Value, Metadata("entry", "Mine"), "Mine\n", TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCode.RevisionConflict, result.Error!.Code);
        Assert.Contains("Edited by hand", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_crash_before_the_rename_leaves_the_original_intact_and_no_temp_file()
    {
        var collection = Open();
        var created = await collection.CreateAsync(Metadata("entry", "Original"), "Original body\n", TestContext.Current.CancellationToken);
        var path = Path.Combine(_root, "memory", "entry.md");
        var original = File.ReadAllText(path);
        AtomicFileWriter.BeforeRename = _ => throw new IOException("simulated crash");

        await Assert.ThrowsAsync<IOException>(async () =>
            await collection.ReplaceAsync(EntryId.Parse("entry"), created.Value, Metadata("entry", "Changed"), "Changed\n", TestContext.Current.CancellationToken));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "memory"), "*.tmp"));
    }

    [Fact]
    public async Task Read_only_collections_refuse_writes()
    {
        var collection = Open(readOnly: true);
        Assert.False(collection.IsWritable);
        var result = await collection.CreateAsync(Metadata("entry", "Plain"), "", TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.ReadOnly, result.Error!.Code);
    }
}

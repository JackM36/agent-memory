using System.Security.Cryptography;
using System.Text;
using AgentBrain.Application.Abstractions.Content;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Abstractions.Storage;
using AgentBrain.Application.Abstractions.Support;
using AgentBrain.Application.Common;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.Revisions;
using AgentBrain.Domain.Validation;
using AgentBrain.Infrastructure.Markdown.Frontmatter;
using AgentBrain.Infrastructure.Markdown.Writing;

namespace AgentBrain.Infrastructure.Markdown.Collections;

/// <summary>
/// A directory of Markdown entries with a <c>collection.json</c> at its root. Every <c>*.md</c> file below
/// the root is an entry, except a root <c>README.md</c> and anything inside directories starting with
/// <c>.</c>. Reads the files directly; nothing is cached between calls.
/// </summary>
public sealed class MarkdownCollection : IEntryReader, IEntryWriter, ICollectionScanner
{
    private const string Readme = "README.md";
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private readonly IContentAnalyzer _analyzer;
    private readonly IWriteCoordinator? _locks;

    private MarkdownCollection(string root, CollectionDescriptor collection, IContentAnalyzer analyzer, IWriteCoordinator? locks)
    {
        Location = root;
        Collection = collection;
        _analyzer = analyzer;
        _locks = locks;
    }

    public CollectionDescriptor Collection { get; }

    public string Location { get; }

    /// <summary>Writable when the collection is not read-only, a lock coordinator is available and the directory accepts writes.</summary>
    public bool IsWritable => !Collection.ReadOnly && _locks is not null && !IsDirectoryReadOnly();

    /// <summary>Opens a collection. Without <paramref name="locks"/> it is read-only for this process.</summary>
    public static Result<MarkdownCollection> Open(string root, IContentAnalyzer analyzer, IWriteCoordinator? locks = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        var full = Path.GetFullPath(root);
        var descriptorPath = Path.Combine(full, CollectionFileReader.FileName);
        if (!File.Exists(descriptorPath))
        {
            return new Error(ErrorCode.ConfigInvalid, $"'{full}' is not a collection: {CollectionFileReader.FileName} is missing.");
        }

        var descriptor = CollectionFileReader.Parse(File.ReadAllText(descriptorPath), descriptorPath);
        return descriptor.IsSuccess ? new MarkdownCollection(full, descriptor.Value, analyzer, locks) : descriptor.Error;
    }

    public async ValueTask<Result<Revision>> CreateAsync(EntryMetadata metadata, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return await WithLockAsync<Revision>(async () =>
        {
            if (FindForWrite(metadata.Id) is not null)
            {
                return new Error(ErrorCode.ValidationFailed, $"'{Collection.Id}/{metadata.Id}' already exists.");
            }

            var relative = $"{WireNames.Kinds.Name(metadata.Kind)}/{metadata.Id}.md";
            var written = await AtomicFileWriter.WriteAsync(Path.Combine(Location, relative), FrontmatterEmitter.Write(EntryMetadataWriter.ToNode(metadata), body), overwrite: false, cancellationToken);
            return written.IsSuccess ? RevisionCalculator.Compute(metadata, body) : written.Error;
        }, cancellationToken);
    }

    public async ValueTask<Result<Revision>> ReplaceAsync(EntryId id, Revision baseRevision, EntryMetadata metadata, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return await WithLockAsync<Revision>(async () =>
        {
            var current = FindForWrite(id);
            if (current is null)
            {
                return Error.NotFound($"'{Collection.Id}/{id}' does not exist.");
            }

            if (current.Revision != baseRevision)
            {
                return Conflict(id, current.Revision);
            }

            var written = await AtomicFileWriter.WriteAsync(Path.Combine(Location, current.Locator!), FrontmatterEmitter.Write(EntryMetadataWriter.ToNode(metadata), body), overwrite: true, cancellationToken);
            return written.IsSuccess ? RevisionCalculator.Compute(metadata, body) : written.Error;
        }, cancellationToken);
    }

    public async ValueTask<Result> DeleteAsync(EntryId id, Revision baseRevision, CancellationToken cancellationToken)
    {
        var result = await WithLockAsync<bool>(() =>
        {
            var current = FindForWrite(id);
            if (current is null)
            {
                return ValueTask.FromResult<Result<bool>>(Error.NotFound($"'{Collection.Id}/{id}' does not exist."));
            }

            if (current.Revision != baseRevision)
            {
                return ValueTask.FromResult<Result<bool>>(Conflict(id, current.Revision));
            }

            File.Delete(Path.Combine(Location, current.Locator!));
            return ValueTask.FromResult<Result<bool>>(true);
        }, cancellationToken);
        return result.IsSuccess ? Result.Success : result.Error;
    }

    private async ValueTask<Result<T>> WithLockAsync<T>(Func<ValueTask<Result<T>>> write, CancellationToken cancellationToken)
    {
        if (Collection.ReadOnly || _locks is null)
        {
            return new Error(ErrorCode.ReadOnly, $"Collection '{Collection.Id}' is read-only.");
        }

        var lease = await _locks.AcquireAsync("collection-" + Collection.Id, LockWait, cancellationToken);
        if (!lease.IsSuccess)
        {
            return lease.Error;
        }

        await using (lease.Value)
        {
            return await write();
        }
    }

    /// <summary>
    /// Re-reads the current file for an ID under the write lock, so the revision check sees external edits
    /// (spec §10.4). A cheap text prefilter avoids parsing files that cannot hold the ID.
    /// </summary>
    private StoredEntry? FindForWrite(EntryId id)
    {
        foreach (var stamp in Scan())
        {
            var text = File.ReadAllText(Path.Combine(Location, stamp.Path));
            if (!text.Contains(id.Value, StringComparison.Ordinal))
            {
                continue;
            }

            var (entry, _, _) = Parse(text, stamp.Path);
            if (entry?.Metadata.Id == id)
            {
                return entry;
            }
        }

        return null;
    }

    private Error Conflict(EntryId id, Revision current) => new(
        ErrorCode.RevisionConflict,
        $"'{Collection.Id}/{id}' changed since it was read (current revision {current}). Fetch it again and re-apply the change.",
        new Dictionary<string, string> { ["currentRevision"] = current.Value });

    private bool IsDirectoryReadOnly()
    {
        try
        {
            return new DirectoryInfo(Location).Attributes.HasFlag(FileAttributes.ReadOnly) && !OperatingSystem.IsWindows();
        }
        catch (IOException)
        {
            return true;
        }
    }

    public IReadOnlyList<FileStamp> Scan()
    {
        if (!Directory.Exists(Location))
        {
            return [];
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = true };
        var stamps = new List<FileStamp>();
        foreach (var file in new DirectoryInfo(Location).EnumerateFiles("*.md", options))
        {
            var relative = Path.GetRelativePath(Location, file.FullName).Replace('\\', '/');
            if (relative == Readme || relative.Split('/').Any(segment => segment.StartsWith('.')))
            {
                continue;
            }

            stamps.Add(new FileStamp(relative, file.Length, file.LastWriteTimeUtc.Ticks));
        }

        stamps.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return stamps;
    }

    public ScannedFile Load(FileStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(Location, stamp.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScannedFile(stamp, "", null, null, [ValidationIssue.Error("unreadable", "", ex.Message)]);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var (entry, analysis, issues) = Parse(Encoding.UTF8.GetString(bytes), stamp.Path);
        return new ScannedFile(stamp, hash, entry, analysis, issues);
    }

    public ValueTask<StoredEntry?> FindAsync(EntryId id, string? locatorHint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (locatorHint is not null && TryLoad(locatorHint, id) is { } hinted)
        {
            return ValueTask.FromResult<StoredEntry?>(hinted);
        }

        foreach (var stamp in Scan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryLoad(stamp.Path, id) is { } found)
            {
                return ValueTask.FromResult<StoredEntry?>(found);
            }
        }

        return ValueTask.FromResult<StoredEntry?>(null);
    }

    private StoredEntry? TryLoad(string relativePath, EntryId id)
    {
        var path = Path.Combine(Location, relativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        var (entry, _, _) = Parse(File.ReadAllText(path), relativePath);
        return entry?.Metadata.Id == id ? entry : null;
    }

    private (StoredEntry? Entry, ContentAnalysis? Analysis, IReadOnlyList<ValidationIssue> Issues) Parse(string text, string relativePath)
    {
        var (document, error) = FrontmatterParser.Parse(text);
        if (document is null)
        {
            return (null, null, [ValidationIssue.Error("malformed_file", "", error!.Message, error.Line)]);
        }

        var read = EntryMetadataReader.Read(document.Metadata);
        var issues = new List<ValidationIssue>(read.Issues);
        if (read.Metadata is null)
        {
            return (null, null, issues);
        }

        var validation = EntryValidator.Validate(read.Metadata, Collection);
        issues.AddRange(validation.Issues);
        if (!validation.IsValid)
        {
            return (null, null, issues);
        }

        if (Path.GetFileNameWithoutExtension(relativePath) != read.Metadata.Id.Value)
        {
            issues.Add(ValidationIssue.Warning(IssueCodes.FileNameMismatch, FieldNames.Id,
                $"The file name does not match the entry ID '{read.Metadata.Id}'. Identity comes from the ID; renaming the file is optional."));
        }

        var entry = new StoredEntry(
            Collection.Id,
            read.Metadata,
            validation.Applicability!,
            document.Body,
            RevisionCalculator.Compute(read.Metadata, document.Body),
            relativePath);
        return (entry, _analyzer.Analyze(document.Body), issues);
    }
}

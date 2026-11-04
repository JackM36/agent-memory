using System.Text.Json;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Identity;
using AgentBrain.Domain.Metadata;
using AgentBrain.Domain.References;
using AgentBrain.Infrastructure.Markdown.Collections;
using AgentBrain.Infrastructure.Markdown.Frontmatter;

namespace AgentBrain.Infrastructure.Markdown.Tests;

/// <summary>
/// Keeps the evaluation corpus (eval/) honest before any ranking code exists: every entry is valid
/// under the real schema, IDs are unique, links resolve, and every case refers to real entries.
/// </summary>
public sealed class EvaluationCorpusTests
{
    private static readonly string EvalRoot = FindEvalRoot();

    private static readonly Lazy<Dictionary<string, (CollectionDescriptor Collection, EntryMetadata Metadata, string Body)>> Corpus = new(() => Load("corpus"));

    public static TheoryData<string> CorpusFiles() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(EvalRoot, "corpus"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(EvalRoot, "corpus-mirror"), "*.md", SearchOption.AllDirectories))
            .Select(p => Path.GetRelativePath(EvalRoot, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Every_entry_is_valid_in_its_collection(string relativePath)
    {
        var path = Path.Combine(EvalRoot, relativePath);
        var collection = ReadCollection(Path.GetDirectoryName(Path.GetDirectoryName(path))!);

        var (document, error) = FrontmatterParser.Parse(File.ReadAllText(path));
        Assert.Null(error);
        var read = EntryMetadataReader.Read(document!.Metadata);
        Assert.True(read.Metadata is not null, string.Join("; ", read.Issues));
        var validation = EntryValidator.Validate(read.Metadata, collection);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));
        Assert.Equal(Path.GetFileNameWithoutExtension(path), read.Metadata.Id.Value);
    }

    [Fact]
    public void Entry_ids_are_unique_within_each_collection()
    {
        foreach (var group in Directory.EnumerateDirectories(Path.Combine(EvalRoot, "corpus")))
        {
            var ids = Directory.EnumerateFiles(group, "*.md", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Entry_links_in_metadata_and_bodies_resolve()
    {
        foreach (var (reference, entry) in Corpus.Value)
        {
            var links = new[] { entry.Metadata.SupersededBy, entry.Metadata.ResolvedBy, entry.Metadata.Origin }.OfType<EntryRef>().ToList();
            foreach (var match in System.Text.RegularExpressions.Regex.Matches(entry.Body, @"\]\((entry://[^)]+)\)").Cast<System.Text.RegularExpressions.Match>())
            {
                Assert.True(EntryLink.TryParse(match.Groups[1].Value, out var link), $"{reference}: malformed link {match.Groups[1].Value}");
                links.Add(link);
            }

            foreach (var link in links)
            {
                Assert.True(Corpus.Value.ContainsKey(link.WithoutAnchor().ToString()), $"{reference} links missing entry {link}");
            }
        }
    }

    [Fact]
    public void Every_case_refers_to_existing_entries_and_contexts()
    {
        using var contexts = JsonDocument.Parse(File.ReadAllText(Path.Combine(EvalRoot, "cases", "contexts.json")));
        var contextNames = contexts.RootElement.GetProperty("contexts").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(EvalRoot, "cases"), "*.json").Where(f => !f.EndsWith("contexts.json", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var testCase in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                count++;
                var id = testCase.GetProperty("id").GetString()!;
                Assert.True(caseIds.Add(id), $"Duplicate case id {id}");
                Assert.True(contextNames.Contains(testCase.GetProperty("context").GetString()!), $"{id}: unknown context");
                Assert.False(string.IsNullOrWhiteSpace(testCase.GetProperty("query").GetString()), $"{id}: empty query");

                var expectations = testCase.TryGetProperty("variants", out var variants)
                    ? variants.EnumerateArray().Select(v => v.GetProperty("expect")).ToList()
                    : [testCase.GetProperty("expect")];
                foreach (var expect in expectations)
                {
                    foreach (var list in new[] { "relevant", "irrelevant", "blocking", "notBlocking", "conflicts" })
                    {
                        if (!expect.TryGetProperty(list, out var refs))
                        {
                            continue;
                        }

                        foreach (var r in refs.EnumerateArray().Select(e => e.GetString()!))
                        {
                            Assert.True(Corpus.Value.ContainsKey(r), $"{id}: {list} refers to missing entry {r}");
                        }
                    }
                }
            }
        }

        Assert.True(count >= 40, $"Expected at least 40 cases, found {count}.");
    }

    [Fact]
    public void Corpus_covers_every_kind_and_lifecycle_state()
    {
        var entries = Corpus.Value.Values.Select(v => v.Metadata).ToList();
        Assert.All(Enum.GetValues<EntryKind>(), kind => Assert.Contains(entries, e => e.Kind == kind));
        Assert.Contains(entries, e => e.IsBlockingUncertainty);
        Assert.Contains(entries, e => e.Archived is not null && e.SupersededBy is not null);
        Assert.Contains(entries, e => e.Resolution == Resolution.Resolved);
        Assert.Contains(entries, e => e.Publication == Publication.Draft);
        Assert.Contains(entries, e => e.Applies?.Packages?.Count > 0);
    }

    private static Dictionary<string, (CollectionDescriptor, EntryMetadata, string)> Load(string root)
    {
        var entries = new Dictionary<string, (CollectionDescriptor, EntryMetadata, string)>(StringComparer.Ordinal);
        foreach (var collectionDir in Directory.EnumerateDirectories(Path.Combine(EvalRoot, root)))
        {
            var collection = ReadCollection(collectionDir);
            foreach (var file in Directory.EnumerateFiles(collectionDir, "*.md", SearchOption.AllDirectories))
            {
                var (document, _) = FrontmatterParser.Parse(File.ReadAllText(file));
                var metadata = EntryMetadataReader.Read(document!.Metadata).Metadata!;
                entries[new EntryRef(collection.Id, metadata.Id).ToString()] = (collection, metadata, document.Body);
            }
        }

        return entries;
    }

    private static CollectionDescriptor ReadCollection(string directory)
    {
        var path = Path.Combine(directory, CollectionFileReader.FileName);
        var result = CollectionFileReader.Parse(File.ReadAllText(path), path);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(Path.GetFileName(directory), result.Value.Id.Value);
        return result.Value;
    }

    private static string FindEvalRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "eval");
            if (Directory.Exists(Path.Combine(candidate, "corpus")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("eval/corpus not found.");
    }
}

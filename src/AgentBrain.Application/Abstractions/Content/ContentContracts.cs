using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Sections;

namespace AgentBrain.Application.Abstractions.Content;

/// <summary>A Markdown link found in a body.</summary>
public sealed record ContentLink(string Url, string Text);

/// <summary>Structure of an entry body: headings (outside fenced code) and links.</summary>
public sealed record ContentAnalysis(IReadOnlyList<Heading> Headings, IReadOnlyList<ContentLink> Links);

/// <summary>Parses entry bodies. Implemented by the Markdown adapter so Application stays parser-free.</summary>
public interface IContentAnalyzer
{
    ContentAnalysis Analyze(string body);
}

/// <summary>
/// Enumerates and loads the files of one file-backed collection for incremental indexing.
/// Paths are relative to the collection root, with <c>/</c> separators.
/// </summary>
public interface ICollectionScanner
{
    CollectionDescriptor Collection { get; }

    IReadOnlyList<FileStamp> Scan();

    ScannedFile Load(FileStamp stamp);
}

namespace AgentBrain.Domain.Metadata;

/// <summary>
/// Format-neutral metadata tree. Adapters convert their syntax (YAML frontmatter, JSON, database
/// rows) into this tree, so mapping and validation rules exist exactly once. <see cref="Line"/> is the
/// 1-based source line when the adapter knows it.
/// </summary>
public abstract record MetadataNode(int? Line);

/// <summary>A scalar value as text. <see cref="Quoted"/> is true when the source quoted it explicitly.</summary>
public sealed record MetadataScalar(string Value, bool Quoted = false, int? Line = null) : MetadataNode(Line);

public sealed record MetadataNull(int? Line = null) : MetadataNode(Line);

public sealed record MetadataSequence(IReadOnlyList<MetadataNode> Items, int? Line = null) : MetadataNode(Line);

/// <summary>A mapping with its keys in source order (duplicates are kept so the reader can report them).</summary>
public sealed record MetadataMapping(IReadOnlyList<KeyValuePair<string, MetadataNode>> Entries, int? Line = null) : MetadataNode(Line);

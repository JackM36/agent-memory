using System.Diagnostics.CodeAnalysis;

namespace AgentBrain.Domain.Entries;

/// <summary>
/// The lowercase kebab-case spellings used in frontmatter, configuration and contracts. One table per
/// enum keeps parsing and formatting symmetric and reflection-free.
/// </summary>
public static class WireNames
{
    public static readonly WireTable<EntryKind> Kinds = new(
        (EntryKind.Knowledge, "knowledge"), (EntryKind.Memory, "memory"), (EntryKind.Note, "note"),
        (EntryKind.Uncertainty, "uncertainty"), (EntryKind.Report, "report"));

    public static readonly WireTable<ApplicabilityScope> Scopes = new(
        (ApplicabilityScope.General, "general"), (ApplicabilityScope.Project, "project"));

    public static readonly WireTable<ProvenanceSource> Sources = new(
        (ProvenanceSource.Curated, "curated"), (ProvenanceSource.Observed, "observed"));

    public static readonly WireTable<VerificationMethod> VerificationMethods = new(
        (VerificationMethod.Code, "code"), (VerificationMethod.User, "user"), (VerificationMethod.Tests, "tests"),
        (VerificationMethod.RepeatedUse, "repeated-use"), (VerificationMethod.Unverified, "unverified"));

    public static readonly WireTable<Publication> Publications = new(
        (Publication.Draft, "draft"), (Publication.Published, "published"));

    public static readonly WireTable<Resolution> Resolutions = new(
        (Resolution.Unresolved, "unresolved"), (Resolution.Resolved, "resolved"));
}

/// <summary>Bidirectional mapping between enum values and their wire names.</summary>
public sealed class WireTable<T>
    where T : struct, Enum
{
    private readonly Dictionary<T, string> _names = [];
    private readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);

    public WireTable(params (T Value, string Name)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        foreach (var (value, name) in pairs)
        {
            _names.Add(value, name);
            _values.Add(name, value);
        }

        Names = [.. pairs.Select(p => p.Name)];
        Joined = string.Join(", ", Names);
    }

    public IReadOnlyList<string> Names { get; }

    /// <summary>All names, comma-separated, for error messages.</summary>
    public string Joined { get; }

    public string Name(T value) => _names[value];

    public bool TryParse([NotNullWhen(true)] string? text, out T value) => _values.TryGetValue(text ?? "", out value);
}

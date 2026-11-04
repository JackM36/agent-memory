namespace AgentBrain.Domain.Entries;

/// <summary>Entry kinds (spec §5.2). There is deliberately no rule kind.</summary>
public enum EntryKind
{
    Knowledge,
    Memory,
    Note,
    Uncertainty,
    Report,
}

public enum ApplicabilityScope
{
    General,
    Project,
}

/// <summary>Curated content was reviewed for reuse; observed content was recorded from project work.</summary>
public enum ProvenanceSource
{
    Curated,
    Observed,
}

public enum VerificationMethod
{
    Code,
    User,
    Tests,
    RepeatedUse,
    Unverified,
}

public enum Publication
{
    Draft,
    Published,
}

/// <summary>Uncertainty-specific state, separate from publication state (spec §5.4).</summary>
public enum Resolution
{
    Unresolved,
    Resolved,
}

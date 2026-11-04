namespace AgentBrain.Application.Retrieval.Ranking;

/// <summary>
/// Retrieval behaviour version <c>r1</c>: every ranking and admission constant in one place (plan §6.3,
/// spec §7.5). All values are calibration items; Phase 2 values are recorded in ADR 0006 together with
/// the evaluation results that justified them.
/// </summary>
public static class RankingProfileV1
{
    public const string Version = "r1";

    // Admission evidence from structure (added to concept coverage).
    public const double FileMatchBonus = 0.5;
    public const double StrongSymbolBonus = 0.5;
    public const double WeakSymbolBonus = 0.25;
    public const double PackageMatchBonus = 0.15;
    public const double HintTagBonus = 0.1;

    /// <summary>Concept weight for a body-only hit, relative to a hit in title, summary, tags or identifiers.</summary>
    public const double BodyHitWeight = 0.5;

    /// <summary>Extra admission evidence required when the entry's technologies do not match the context (spec §7.3).</summary>
    public const double TechnologyMismatchPenalty = 0.45;

    /// <summary>Mismatch penalty for another project's entry, which already faces the stricter other-project threshold.</summary>
    public const double OtherProjectTechnologyMismatchPenalty = 0.05;

    // Final score terms.
    public const double CoverageWeight = 1.0;
    public const double LexicalRankWeight = 0.5;
    public const double SameProjectBonus = 0.15;
    public const double OtherProjectPenalty = -0.3;
    public const double TechnologyMismatchScore = -0.4;
    public const double CuratedBonus = 0.05;
    public const double VerifiedBonus = 0.05;

    // Blocking uncertainties (A4).
    public const double BlockingCoverageFloor = 0.3;
    public const int MaxBlocking = 3;
}

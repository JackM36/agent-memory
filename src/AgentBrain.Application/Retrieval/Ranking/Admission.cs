namespace AgentBrain.Application.Retrieval.Ranking;

/// <summary>How an entry's applicability relates to the current query context (plan §6.2).</summary>
public enum ApplicabilityClass
{
    SameProject,
    General,
    OtherProject,
}

/// <summary>
/// Admission thresholds <c>T(class, relaxation)</c> (plan §6.3). The contract, pinned by tests:
/// non-increasing in relaxation, and <c>T(other) &gt; T(general) ≥ T(same)</c> at every relaxation.
/// The constants are calibration items, starting values only, settled against the evaluation corpus
/// in Phase 2.
/// </summary>
public static class Admission
{
    // Threshold at relaxation 0 and at relaxation 1, per class. Calibrated in Phase 2 (ADR 0006).
    private const double SameStrict = 0.45;
    private const double SameBroad = 0.05;
    private const double GeneralStrict = 0.55;
    private const double GeneralBroad = 0.15;
    private const double OtherStrict = 0.75;
    private const double OtherBroad = 0.35;

    /// <summary>Minimum admission score for an entry of the given class.</summary>
    public static double Threshold(ApplicabilityClass applicability, Relaxation relaxation)
    {
        var (strict, broad) = applicability switch
        {
            ApplicabilityClass.SameProject => (SameStrict, SameBroad),
            ApplicabilityClass.General => (GeneralStrict, GeneralBroad),
            ApplicabilityClass.OtherProject => (OtherStrict, OtherBroad),
            _ => throw new ArgumentOutOfRangeException(nameof(applicability)),
        };

        return strict + ((broad - strict) * relaxation.Value);
    }

    /// <summary>Below the broad preset, other-project entries also need a descriptive-field hit (plan §6.3).</summary>
    public static bool RequiresDescriptiveHit(ApplicabilityClass applicability, Relaxation relaxation) =>
        applicability == ApplicabilityClass.OtherProject && relaxation.Value < Relaxation.Broad.Value;
}

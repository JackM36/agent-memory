using System.Globalization;
using AgentBrain.Application.Abstractions.Indexing;
using AgentBrain.Application.Retrieval.QueryAnalysis;
using AgentBrain.Domain.Entries;
using AgentBrain.Domain.Matching;
using AgentBrain.Domain.Versions;

namespace AgentBrain.Application.Retrieval.Ranking;

/// <summary>A candidate after admission and scoring.</summary>
public sealed record ScoredCandidate(IndexCandidate Candidate, ApplicabilityClass Applicability, double Score, string? Explanation);

/// <summary>
/// Plan §6.1 stages 4–7: extract features, admit, score, and pick blocking uncertainties. Admission
/// uses query-relative evidence (concept coverage weighted by rarity, plus structural matches), never
/// raw BM25, so thresholds mean the same thing for every query.
/// </summary>
public static class Ranker
{
    public static (IReadOnlyList<ScoredCandidate> Results, IReadOnlyList<ScoredCandidate> Blocking) Rank(
        CandidateSet set, QueryTerms terms, QueryContext context, Relaxation relaxation, bool explain)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(context);

        // Rarer concepts weigh more. A concept no entry contains (a typo, or a word this corpus never uses)
        // cannot tell entries apart: it counts with the average weight of the known concepts rather than the
        // maximum rarity weight, so it lowers coverage without dominating it.
        var known = set.ConceptDocumentFrequency.Where(df => df > 0).Select(df => Math.Log(1 + (set.TotalEntries / (df + 1.0)))).ToList();
        var unknownWeight = known.Count > 0 ? known.Average() : 0;
        var weights = set.ConceptDocumentFrequency.Select(df => df == 0 ? unknownWeight : Math.Log(1 + (set.TotalEntries / (df + 1.0)))).ToList();
        var lexicalCount = Math.Max(1, set.Candidates.Count(c => c.LexicalRank is not null));
        var results = new List<ScoredCandidate>();
        var blocking = new List<ScoredCandidate>();

        foreach (var candidate in set.Candidates)
        {
            var f = Extract(candidate, terms, context, weights, lexicalCount);
            var score = Score(f, candidate.Entry.Metadata);
            var explanation = explain ? Explain(f, relaxation, score) : null;
            var scored = new ScoredCandidate(candidate, f.Applicability, score, explanation);

            if (candidate.Entry.Metadata.IsBlockingUncertainty)
            {
                var structural = f.FileMatch || f.Symbol != MatchStrength.None;
                if (f.Applicability != ApplicabilityClass.OtherProject && (structural || f.Coverage >= RankingProfileV1.BlockingCoverageFloor))
                {
                    blocking.Add(scored);
                }

                continue;
            }

            if (Admitted(f, relaxation))
            {
                results.Add(scored);
            }
        }

        return (Order(results), Order(blocking).Take(RankingProfileV1.MaxBlocking).ToList());
    }

    private static List<ScoredCandidate> Order(List<ScoredCandidate> candidates) =>
    [
        .. candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Applicability)
            .ThenBy(c => c.Candidate.Entry.Ref.Collection.Value, StringComparer.Ordinal)
            .ThenBy(c => c.Candidate.Entry.Ref.Entry.Value, StringComparer.Ordinal),
    ];

    private static bool Admitted(FeatureSet f, Relaxation relaxation)
    {
        if (f.AdmissionScore <= 0)
        {
            return false;
        }

        // Another project's entry already faces the stricter other-project threshold; its technology mismatch
        // adds only a smaller penalty, so strong task relevance can still qualify it (spec §7.3).
        var mismatch = !f.TechnologyMismatch ? 0
            : f.Applicability == ApplicabilityClass.OtherProject ? RankingProfileV1.OtherProjectTechnologyMismatchPenalty
            : RankingProfileV1.TechnologyMismatchPenalty;
        var required = Admission.Threshold(f.Applicability, relaxation) + mismatch;
        if (f.AdmissionScore < required)
        {
            return false;
        }

        return !Admission.RequiresDescriptiveHit(f.Applicability, relaxation) || f.DescriptiveHit;
    }

    private static double Score(FeatureSet f, EntryMetadata metadata)
    {
        var score = (RankingProfileV1.CoverageWeight * f.Coverage) + (RankingProfileV1.LexicalRankWeight * f.LexicalRank) + f.Structural;
        score += f.Applicability switch
        {
            ApplicabilityClass.SameProject => RankingProfileV1.SameProjectBonus,
            ApplicabilityClass.OtherProject => RankingProfileV1.OtherProjectPenalty,
            _ => 0,
        };
        if (f.TechnologyMismatch)
        {
            score += RankingProfileV1.TechnologyMismatchScore;
        }

        if (metadata.Source == ProvenanceSource.Curated)
        {
            score += RankingProfileV1.CuratedBonus;
        }

        if (metadata.Verified is { By: not VerificationMethod.Unverified })
        {
            score += RankingProfileV1.VerifiedBonus;
        }

        return score;
    }

    private static FeatureSet Extract(IndexCandidate candidate, QueryTerms terms, QueryContext context, List<double> weights, int lexicalCount)
    {
        var entry = candidate.Entry;
        double covered = 0, total = 0;
        var descriptive = false;
        for (var i = 0; i < weights.Count; i++)
        {
            total += weights[i];
            var hit = candidate.Hits.FirstOrDefault(h => h.ConceptIndex == i);
            if (hit is not null)
            {
                covered += weights[i] * (hit.Descriptive ? 1.0 : RankingProfileV1.BodyHitWeight);
                descriptive |= hit.Descriptive;
            }
        }

        var coverage = total > 0 ? covered / total : 0;
        var lexicalRank = candidate.LexicalRank is { } rank ? 1.0 - ((rank - 1.0) / lexicalCount) : 0;

        var fileMatch = context.Files is { Count: > 0 } files
            && entry.Metadata.Files.Any(g => GlobPattern.TryParse(g, out var glob) && files.Any(glob.IsMatch));

        var symbol = MatchStrength.None;
        foreach (var requested in context.Symbols ?? [])
        {
            foreach (var declared in entry.Metadata.Symbols)
            {
                var strength = SymbolMatcher.Match(declared, requested);
                symbol = strength > symbol ? strength : symbol;
            }
        }

        var packageMatch = context.Packages is { Count: > 0 } packages
            && entry.Applicability.Packages.Any(p => packages.TryGetValue(p.Name, out var version)
                && VersionRange.Check(p.Versions, version) != VersionCompatibility.Incompatible);
        var hintMatch = context.HintTags is { Count: > 0 } hints && entry.Metadata.Tags.Any(hints.Contains);

        var structural = (fileMatch ? RankingProfileV1.FileMatchBonus : 0)
            + symbol switch { MatchStrength.Strong => RankingProfileV1.StrongSymbolBonus, MatchStrength.Weak => RankingProfileV1.WeakSymbolBonus, _ => 0 }
            + (packageMatch ? RankingProfileV1.PackageMatchBonus : 0)
            + (hintMatch ? RankingProfileV1.HintTagBonus : 0);

        var applicability = Classify(entry.Applicability, context.ProjectId);
        var entryTechnologies = entry.Applicability.Technologies;
        var mismatch = entryTechnologies.Count > 0
            && context.Technologies is { Count: > 0 } contextTechnologies
            && !entryTechnologies.Intersect(contextTechnologies, StringComparer.Ordinal).Any()
            && !QueryAnalyzer.Mentions(terms, entryTechnologies);

        return new FeatureSet(coverage, descriptive, lexicalRank, structural, fileMatch, symbol, applicability, mismatch);
    }

    public static ApplicabilityClass Classify(Applicability applicability, string? projectId)
    {
        ArgumentNullException.ThrowIfNull(applicability);
        if (applicability.Scope == ApplicabilityScope.General)
        {
            return ApplicabilityClass.General;
        }

        return projectId is not null && applicability.Projects.Contains(projectId) ? ApplicabilityClass.SameProject : ApplicabilityClass.OtherProject;
    }

    private static string Explain(FeatureSet f, Relaxation relaxation, double score) => string.Create(
        CultureInfo.InvariantCulture,
        $"coverage={f.Coverage:0.00} lexical={f.LexicalRank:0.00} structural={f.Structural:0.00} class={f.Applicability} " +
        $"techMismatch={f.TechnologyMismatch} admission={f.AdmissionScore:0.00}/{Admission.Threshold(f.Applicability, relaxation):0.00} score={score:0.00}");

    private sealed record FeatureSet(
        double Coverage,
        bool DescriptiveHit,
        double LexicalRank,
        double Structural,
        bool FileMatch,
        MatchStrength Symbol,
        ApplicabilityClass Applicability,
        bool TechnologyMismatch)
    {
        public double AdmissionScore => Coverage + Structural;
    }
}

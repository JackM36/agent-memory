using AgentBrain.Application.Common;
using AgentBrain.Application.Retrieval;
using AgentBrain.Application.Retrieval.Ranking;

namespace AgentBrain.Application.Tests;

public sealed class RetrievalContractTests
{
    public static TheoryData<ApplicabilityClass> Classes => [ApplicabilityClass.SameProject, ApplicabilityClass.General, ApplicabilityClass.OtherProject];

    [Theory]
    [MemberData(nameof(Classes))]
    public void Admission_threshold_never_increases_with_relaxation(ApplicabilityClass applicability)
    {
        var previous = double.MaxValue;
        for (var step = 0; step <= 100; step++)
        {
            var threshold = Admission.Threshold(applicability, Relaxation.FromValue(step / 100.0));
            Assert.True(threshold <= previous, $"Threshold rose at relaxation {step / 100.0}.");
            previous = threshold;
        }
    }

    [Fact]
    public void Other_project_content_always_needs_more_evidence()
    {
        for (var step = 0; step <= 100; step++)
        {
            var r = Relaxation.FromValue(step / 100.0);
            Assert.True(Admission.Threshold(ApplicabilityClass.OtherProject, r) > Admission.Threshold(ApplicabilityClass.General, r));
            Assert.True(Admission.Threshold(ApplicabilityClass.General, r) >= Admission.Threshold(ApplicabilityClass.SameProject, r));
        }
    }

    [Fact]
    public void Descriptive_hits_are_required_for_other_projects_below_broad()
    {
        Assert.True(Admission.RequiresDescriptiveHit(ApplicabilityClass.OtherProject, Relaxation.Default));
        Assert.False(Admission.RequiresDescriptiveHit(ApplicabilityClass.OtherProject, Relaxation.Broad));
        Assert.False(Admission.RequiresDescriptiveHit(ApplicabilityClass.General, Relaxation.Strict));
    }

    [Theory]
    [InlineData("strict", 0.0)]
    [InlineData("default", 0.35)]
    [InlineData("broad", 0.8)]
    [InlineData("0.5", 0.5)]
    [InlineData("1", 1.0)]
    public void Relaxation_parses_presets_and_numbers(string text, double expected)
    {
        Assert.True(Relaxation.TryParse(text, out var relaxation));
        Assert.Equal(expected, relaxation.Value, 3);
    }

    [Theory]
    [InlineData("loose")]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("")]
    public void Relaxation_rejects_out_of_range_values(string text) => Assert.False(Relaxation.TryParse(text, out _));

    [Fact]
    public void Presets_are_ordered() =>
        Assert.True(Relaxation.Strict.Value < Relaxation.Default.Value && Relaxation.Default.Value < Relaxation.Broad.Value);

    [Fact]
    public void Results_carry_either_a_value_or_an_error()
    {
        Result<int> ok = 3;
        Result<int> failed = Error.NotFound("missing");

        Assert.True(ok.IsSuccess);
        Assert.Equal(3, ok.Value);
        Assert.False(failed.IsSuccess);
        Assert.Equal(ErrorCode.NotFound, failed.Error.Code);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
    }

    [Fact]
    public void Every_error_code_has_a_wire_name()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            Assert.Matches("^[a-z_]+$", ErrorCodes.Names.Name(code));
        }
    }
}

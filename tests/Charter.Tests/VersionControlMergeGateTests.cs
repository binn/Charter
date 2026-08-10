using Charter.VersionControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Charter.Tests;

/// <summary>
/// The merge gate of change spec 001 part A.5 — the part of part A that matters most.
/// </summary>
/// <remarks>
/// Specification section 7.4 puts merge authority in provider-side branch protection, outside
/// Charter's trust boundary. These tests hold the line that the guarantee is reported as strong
/// <em>only</em> when it actually is: the provider can enforce it, and this repository has turned it
/// on. Everything else is advisory and says so.
/// </remarks>
public class VersionControlMergeGateTests
{
    [Fact]
    public async Task AProtectedRepositoryOnACapableProviderKeepsTheV1Guarantee()
    {
        var provider = new FakeVersionControlProvider();
        provider.Protection["main"] = new BranchProtectionStatus(
            true,
            RequiresReview: true,
            RequiredApprovals: 1,
            CodeOwnersReviewRequired: true);

        var assessment = await Inspect(provider);

        Assert.True(assessment.IsEnforced);
        Assert.Equal(MergeGateEnforcement.ProviderEnforced, assessment.Effective);
        Assert.Equal("provider_enforced", assessment.EffectiveName);
        Assert.Null(assessment.Warning);
    }

    [Fact]
    public async Task ACapableProviderWithNoRuleConfiguredIsAdvisoryAndSaysSo()
    {
        // Part A.5: "A GitHub repo with no branch protection rule is functionally advisory too, and
        // should be flagged as such." Supported is not configured.
        var provider = new FakeVersionControlProvider();

        var assessment = await Inspect(provider);

        Assert.False(assessment.IsEnforced);
        Assert.Equal(MergeGateEnforcement.ProviderEnforced, assessment.Declared);
        Assert.Equal(MergeGateEnforcement.Advisory, assessment.Effective);

        Assert.NotNull(assessment.Warning);
        Assert.Contains("nothing stops a person", assessment.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Charter will not do it", assessment.Warning, StringComparison.Ordinal);
        Assert.Contains("cannot prevent it either", assessment.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtectionRuleThatDoesNotRequireReviewIsNotAMergeGate()
    {
        // A rule that only blocks force pushes is a rule. It is not a merge gate, and calling it one
        // would overstate the exact property section 7.4 rests on.
        var provider = new FakeVersionControlProvider();
        provider.Protection["main"] = new BranchProtectionStatus(true, RequiresReview: false);

        var assessment = await Inspect(provider);

        Assert.False(assessment.IsEnforced);
        Assert.Equal("advisory", assessment.EffectiveName);
    }

    [Fact]
    public async Task AnAdvisoryProviderIsAdvisoryEvenWhereProtectionWouldBeReported()
    {
        // Part A.6's bare git remote: no review surface, no enforcement, and the wording names the
        // provider rather than the repository, because the repository cannot fix it.
        var provider = new FakeVersionControlProvider
        {
            Id = "git",
            DeclaredCapabilities = VersionControlCapabilities.None,
        };

        var assessment = await Inspect(provider);

        Assert.Equal(MergeGateEnforcement.Advisory, assessment.Declared);
        Assert.False(assessment.IsEnforced);
        Assert.Contains("cannot enforce review", assessment.Warning!, StringComparison.Ordinal);
        Assert.Equal(BranchProtectionStatus.Unsupported, assessment.Protection);
    }

    [Fact]
    public async Task AProviderThatThrowsIsReportedAsNotVerifiedRatherThanAsProtected()
    {
        // Failing closed is the only acceptable direction here. An unanswered question about the
        // merge gate must never read as "protected".
        var provider = new FakeVersionControlProvider
        {
            ProtectionFailure = new InvalidOperationException("the provider is unreachable"),
        };

        var assessment = await Inspect(provider);

        Assert.False(assessment.IsEnforced);
        Assert.False(assessment.Protection.Configured);
        Assert.Contains("not verified", assessment.Protection.Detail!, StringComparison.Ordinal);
    }

    private static async Task<MergeGateAssessment> Inspect(IVersionControlProvider provider)
    {
        var inspector = new MergeGateInspector(
            new VersionControlProviderRegistry([provider]),
            NullLogger<MergeGateInspector>.Instance);

        return await inspector.AssessAsync(
            provider,
            VersionControlTestFixtures.RepoRef(provider.Id),
            TestContext.Current.CancellationToken);
    }
}

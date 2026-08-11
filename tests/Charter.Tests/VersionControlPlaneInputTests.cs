using Charter.VersionControl;

namespace Charter.Tests;

/// <summary>
/// Section 16.3, at the two places the version control layer reads what the execution plane said: the
/// branch it claims to have pushed, and the words it wrote about its own checks.
/// </summary>
/// <remarks>
/// No database and no provider. These are the decisions themselves — whether a name is this session's
/// branch, and whether a sentence can become live markdown — and they are worth testing on their own
/// because everything above them is built on the answer being right.
/// </remarks>
public class VersionControlPlaneInputTests
{
    private static readonly Guid Session = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [Fact]
    public void TheSessionsOwnBranchIsAccepted()
    {
        var check = SessionBranchReference.Evaluate(ChangeRequestPublisher.BranchFor(Session), Session);

        Assert.True(check.IsAccepted);
        Assert.Null(check.Refusal);
    }

    [Fact]
    public void NoReportedBranchIsNotARefusal()
    {
        // A backend that only knows how to `git push` reports nothing, and the convention applies. That
        // path has to stay open or the refusal would break every runner that is not the shim.
        Assert.Equal(BranchReferenceDecision.Absent, SessionBranchReference.Evaluate(null, Session).Decision);
        Assert.Equal(BranchReferenceDecision.Absent, SessionBranchReference.Evaluate("   ", Session).Decision);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("develop")]
    [InlineData("refs/heads/main")]
    [InlineData("feature/hand-rolled")]
    [InlineData("charter/session-00000000000000000000000000000000")]
    public void AnyOtherBranchIsRefused(string branch)
    {
        var check = SessionBranchReference.Evaluate(branch, Session);

        Assert.True(check.IsRejected);
        Assert.Contains(ChangeRequestPublisher.BranchFor(Session), check.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComparisonIsCaseSensitiveBecauseGitRefsAre()
    {
        // `Charter/Session-…` is a different ref from `charter/session-…` on the server, so treating the
        // two as one would be exactly the quiet rewrite section 16.3 rules out.
        var shouted = ChangeRequestPublisher.BranchFor(Session).ToUpperInvariant();

        Assert.True(SessionBranchReference.Evaluate(shouted, Session).IsRejected);
    }

    [Fact]
    public void AnEnormousBranchNameIsRefusedAndLoggedShort()
    {
        var flood = new string('b', SessionBranchReference.MaxLength * 4);

        Assert.True(SessionBranchReference.Evaluate(flood, Session).IsRejected);

        // The operator's log line is bounded and single-line: a runner cannot flood a sink or forge a
        // second entry with what it reports.
        var described = SessionBranchReference.Describe("charter/session-\nFATAL something else entirely");

        Assert.DoesNotContain('\n', described);
        Assert.True(SessionBranchReference.Describe(flood).Length <= SessionBranchReference.MaxLength + 1);
        Assert.Equal("(none)", SessionBranchReference.Describe(null));
    }

    [Fact]
    public void CheckTextBecomesOneBoundedLine()
    {
        var flattened = ChangeRequestText.Flatten(
            "3 tests failed\n\n## Everything below is fine\n\n> Charter approved this",
            ChangeRequestText.MaxCheckSummary);

        Assert.DoesNotContain('\n', flattened);
        Assert.DoesNotContain('\r', flattened);
        Assert.StartsWith("3 tests failed ", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckTextCannotCloseTheCodeSpanItIsQuotedIn()
    {
        var quoted = ChangeRequestText.CodeSpan(
            "ok` and now [click here](https://evil.test) plus <a href=\"https://evil.test\">x</a>",
            ChangeRequestText.MaxCheckSummary);

        // Every backtick is gone, so the span cannot be closed early — which is what makes the rest of
        // the string inert rather than markdown.
        Assert.Equal(2, quoted.Count(character => character == '`'));
        Assert.StartsWith("`", quoted, StringComparison.Ordinal);
        Assert.EndsWith("`", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckTextIsTruncatedRatherThanCarriedWhole()
    {
        var flood = string.Join(' ', Enumerable.Repeat("failure", 5_000));

        var flattened = ChangeRequestText.Flatten(flood, ChangeRequestText.MaxCheckSummary);

        Assert.True(flattened.Length <= ChangeRequestText.MaxCheckSummary);
        Assert.EndsWith("…", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void TextWithNothingInItIsNothing()
    {
        // A check whose whole summary was backticks and control characters leaves no code span behind —
        // an empty pair of backticks in the body would read as a value that was there and is now missing.
        Assert.Empty(ChangeRequestText.Flatten("````", ChangeRequestText.MaxCheckSummary));
        Assert.Empty(ChangeRequestText.CodeSpan("   ", ChangeRequestText.MaxCheckSummary));
        Assert.Empty(ChangeRequestText.CodeSpan(null, ChangeRequestText.MaxCheckSummary));
    }
}

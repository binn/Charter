using System.Text;

namespace Charter.Refinement;

/// <summary>
/// Everything the agent is told about a piece of work — and the boundary that guarantees it is all
/// the agent is told (section 16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The agent never sees raw requester text.</strong> Section 16 calls this Charter's
/// strongest security property, and it is structural rather than procedural: the only way to build
/// an <see cref="AgentBriefing"/> is <see cref="For"/>, whose single parameter is an
/// <see cref="ApprovedSpec"/>. There is no overload taking a <see cref="string"/>, a
/// <see cref="RequesterText"/> or a <c>Request</c>, so "just append what they actually said" is not
/// an edit somebody can make without adding a new API and being asked why in review.
/// </para>
/// <para>
/// Every line of <see cref="Text"/> is composed from <see cref="SpecDocument"/> fields, all of which
/// are model-authored during refinement and confirmed by a named human. Nothing here reads the
/// conversation transcript, and the transcript's requester turns could not be read as plain strings
/// even if it did (see <see cref="ConversationTurn.AuthoredText"/>).
/// </para>
/// <para>
/// This is a layer, not a magic wand. Section 16.2 is explicit that repo-content injection is the
/// harder half and remains open; what closes here is the requester-text half.
/// </para>
/// </remarks>
public sealed class AgentBriefing
{
    private AgentBriefing(ApprovedSpec approved, string text)
    {
        Source = approved;
        Text = text;
    }

    /// <summary>The confirmed spec this briefing was generated from.</summary>
    public ApprovedSpec Source { get; }

    /// <summary>The briefing text. Composed only from the structured spec.</summary>
    public string Text { get; }

    /// <summary>The contract, verbatim — the same criteria the requester confirmed.</summary>
    public IReadOnlyList<string> AcceptanceCriteria => Source.Spec.AcceptanceCriteria;

    /// <summary>Where the change is expected to land.</summary>
    public SpecScope Scope => Source.Spec.Scope;

    /// <summary>The named human accountable for this run (section 7.3, guardrail 5).</summary>
    public Guid AuthorisedBy => Source.ConfirmedBy;

    /// <summary>
    /// Builds the briefing. The only entry point, and it takes a confirmed spec — nothing else.
    /// </summary>
    public static AgentBriefing For(ApprovedSpec approved)
    {
        ArgumentNullException.ThrowIfNull(approved);

        var spec = approved.Spec;
        var builder = new StringBuilder()
            .AppendLine("You are implementing an approved specification.")
            .AppendLine()
            .Append("# ").AppendLine(spec.Title)
            .AppendLine()
            .AppendLine("## Outcome")
            .AppendLine()
            .AppendLine(spec.Outcome)
            .AppendLine()
            .AppendLine("## Acceptance criteria")
            .AppendLine()
            .AppendLine(SpecRenderer.AcceptanceCriteriaBlock(spec.AcceptanceCriteria));

        if (spec.TechnicalApproach is { Length: > 0 } approach)
        {
            builder.AppendLine()
                .AppendLine("## Technical approach")
                .AppendLine()
                .AppendLine(approach);
        }

        if (!spec.Scope.IsEmpty)
        {
            builder.AppendLine().AppendLine("## Scope").AppendLine();
            foreach (var path in spec.Scope.All)
            {
                builder.Append("- ").AppendLine(path);
            }
        }

        if (spec.Risks.Count > 0)
        {
            builder.AppendLine().AppendLine("## Risks").AppendLine();
            foreach (var risk in spec.Risks)
            {
                builder.Append("- ").AppendLine(risk);
            }
        }

        return new AgentBriefing(approved, builder.ToString().TrimEnd() + "\n");
    }
}

namespace Charter.Api;

/// <summary>
/// The dotted verbs the HTTP API writes to <c>audit_logs</c> (section 7.3, guardrail 5).
/// </summary>
/// <remarks>
/// <para>
/// Constants rather than free strings, for the same reason <c>Charter.Auth.AuditActions</c> is: a
/// rename becomes a compile error, and a reader can see the vocabulary in one place. This is the API
/// layer's half of that vocabulary; the authorisation and identity layer owns its own.
/// </para>
/// <para>
/// Every action here is a human's. Section 7.3 is explicit that the agent never acts on its own
/// initiative, so each of these rows carries an actor — and two of them are read back rather than
/// merely written, which is why the spelling matters as much as the fact.
/// </para>
/// </remarks>
public static class ApiAuditActions
{
    /// <summary>
    /// Section 7.5: an engineer took the branch over and Charter stopped touching it.
    /// </summary>
    /// <remarks>
    /// Read back by the request projection to name who did it. The session row records that a
    /// hand-off happened; this row records the named human it is attributable to.
    /// </remarks>
    public const string SessionHandedOff = "session.handed_off";

    /// <summary>Section 7.5: an engineer sent a running session a new instruction.</summary>
    public const string SessionSteered = "session.steered";

    /// <summary>Section 7.5: the spec was forked and rebuilt onto the same branch.</summary>
    public const string SessionRevised = "session.revised";

    /// <summary>Section 7.5: an engineer marked the work reviewed.</summary>
    public const string SessionApproved = "session.approved";

    /// <summary>Section 33.3 step 1: an admin generated a pairing token.</summary>
    public const string RunnerPairingTokenIssued = "runner.pairing_token.issued";

    /// <summary>Section 33.3 step 5: an agent was revoked, killing its in-flight jobs.</summary>
    public const string RunnerRevoked = "runner.revoked";

    /// <summary>
    /// Section 30.2: the admin checklist was dismissed.
    /// </summary>
    /// <remarks>
    /// Read back as the dismissal itself. A dismissal is a server-side fact (there is no browser
    /// storage in this app), it is attributable to one admin, and it is append-only — which is three
    /// of the audit log's properties and none of a settings column's. See the report: a column on the
    /// organisation would be a better home, and it is an entity this work does not own.
    /// </remarks>
    public const string SetupChecklistDismissed = "setup.checklist.dismissed";
}

namespace Charter.Auth.Authorization;

/// <summary>
/// The answer to a single authorisation question, carrying why.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default value is a denial.</b> This is the whole reason the type is a struct with a
/// private constructor rather than a <c>bool</c>: <c>default(AuthorizationDecision)</c>,
/// <c>new AuthorizationDecision()</c> and the value left in an uninitialised field are all
/// <see cref="IsDenied"/>. Section 7.3 makes deny-by-default a structural property of the code
/// rather than a convention a future policy branch can forget to honour, and a policy method that
/// falls off the end of its own logic denies rather than accidentally granting.
/// </para>
/// <para>
/// A grant may carry an <see cref="AuditAction"/>. Section 7.3, guardrail 5: every action is
/// attributable to a named human, so a decision that grants something notable arrives at the audit
/// writer already labelled instead of relying on the caller to remember.
/// </para>
/// </remarks>
public readonly record struct AuthorizationDecision
{
    /// <summary>What an unpopulated decision says. Nothing granted anything, so nothing is allowed.</summary>
    public const string NoGrantReason = "no grant covers this action";

    private readonly bool allowed;
    private readonly string? reason;
    private readonly string? auditAction;

    private AuthorizationDecision(bool allowed, string reason, string? auditAction)
    {
        this.allowed = allowed;
        this.reason = reason;
        this.auditAction = auditAction;
    }

    /// <summary>True only when something explicitly granted this.</summary>
    public bool IsAllowed => allowed;

    /// <summary>The inverse of <see cref="IsAllowed"/>, and what <c>default</c> reports.</summary>
    public bool IsDenied => !allowed;

    /// <summary>Plain language, safe to show a person. Never a stack trace, never an internal id.</summary>
    public string Reason => reason ?? NoGrantReason;

    /// <summary>
    /// The dotted verb to write to <c>audit_logs</c> when this grant is exercised, or <c>null</c>
    /// when the decision is routine. Always <c>null</c> on a denial: a refusal grants nothing.
    /// </summary>
    public string? AuditAction => allowed ? auditAction : null;

    /// <summary>True when this grant should leave an audit trail.</summary>
    public bool IsNotable => AuditAction is not null;

    /// <summary>An explicit refusal. Identical in effect to <c>default</c>, but says why.</summary>
    public static AuthorizationDecision Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AuthorizationDecision(allowed: false, reason, auditAction: null);
    }

    /// <summary>A grant, optionally labelled with the audit verb it should be recorded under.</summary>
    public static AuthorizationDecision Allow(string reason, string? auditAction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AuthorizationDecision(allowed: true, reason, auditAction);
    }

    /// <summary>The denial <c>default</c> produces, spelled out for readers.</summary>
    public static AuthorizationDecision Denied => default;

    /// <summary>
    /// The stricter of two decisions. Composition never loosens: one denial denies the pair, which
    /// is what lets a repository-level rule be layered over an organisation-level one (section 7.5).
    /// </summary>
    public AuthorizationDecision And(AuthorizationDecision other)
        => IsDenied ? this : other.IsDenied ? other : this;

    /// <inheritdoc />
    public override string ToString() => $"{(allowed ? "allow" : "deny")}: {Reason}";
}

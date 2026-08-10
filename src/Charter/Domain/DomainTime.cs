namespace Charter.Domain;

/// <summary>
/// Timestamp helpers shared by every entity factory.
/// </summary>
/// <remarks>
/// Section 5 stores every timestamp as <c>timestamptz</c>, so the domain normalises to UTC on the
/// way in rather than trusting whatever offset a caller happened to hold. Factories take an
/// optional instant so tests can be deterministic without a clock abstraction.
/// </remarks>
internal static class DomainTime
{
    public static DateTimeOffset Resolve(DateTimeOffset? value)
        => (value ?? DateTimeOffset.UtcNow).ToUniversalTime();

    public static DateTimeOffset? ResolveOptional(DateTimeOffset? value)
        => value?.ToUniversalTime();
}

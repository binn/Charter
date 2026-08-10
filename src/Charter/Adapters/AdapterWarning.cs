namespace Charter.Adapters;

/// <summary>
/// Something Charter did not understand in an adapter file, and chose to keep going past.
/// </summary>
/// <remarks>
/// Section 8: unknown keys warn, never fail, so an old Charter does not break on an adapter file
/// written for a newer one. Warnings are collected and surfaced — logged once at startup and exposed
/// on <see cref="IAdapterCatalog.Warnings"/> — rather than swallowed. A key that vanishes silently is
/// indistinguishable from a key that was applied.
/// </remarks>
public sealed record AdapterWarning(string SourcePath, string Field, string Message)
{
    public override string ToString() => $"{SourcePath}: {Field}: {Message}";
}

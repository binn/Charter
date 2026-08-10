using System.Text.Json;
using Charter.Domain;

namespace Charter.Auth.Authorization;

/// <summary>
/// Reads the auto-dispatch restriction out of a repository's stored <c>.charter/config.yml</c>
/// snapshot (section 8).
/// </summary>
/// <remarks>
/// <para>
/// The committed file is the source of truth and the snapshot is a copy, which is exactly why this
/// only ever produces a <see cref="RepoAutoDispatchRestriction"/> — a type that can tighten and
/// cannot loosen. Whatever the file says, the worst it can do to organisation policy is narrow it.
/// </para>
/// <para>
/// Section 8: unknown keys warn, never fail, so an old Charter reading a newer file keeps working.
/// A snapshot that is present but unparseable is a different matter and fails closed: auto-dispatch
/// is disallowed until somebody looks at it, because the alternative is dispatching under a
/// guardrail nobody could read.
/// </para>
/// </remarks>
public static class RepoCharterConfig
{
    /// <summary>Projects the snapshot into a restriction, or <see cref="RepoAutoDispatchRestriction.None"/>.</summary>
    public static RepoAutoDispatchRestriction ReadAutoDispatchRestriction(string? configSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(configSnapshotJson))
        {
            return RepoAutoDispatchRestriction.None;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(configSnapshotJson);
        }
        catch (JsonException)
        {
            return new RepoAutoDispatchRestriction { DisallowAutoDispatch = true };
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new RepoAutoDispatchRestriction { DisallowAutoDispatch = true };
            }

            var root = document.RootElement;

            // Section 7.5: allowed_paths is a subset of the repo scope, never a superset, so the
            // repository's own allow list is a ceiling on any policy's paths.
            var scopeAllow = ReadStringArray(root, "scopes", "allow");

            var autoDispatch = TryGetObject(root, "auto_dispatch");

            return new RepoAutoDispatchRestriction
            {
                DisallowAutoDispatch = autoDispatch is { } block && ReadBool(block, "enabled") == false,
                MaxCostUsdCeiling = ReadDecimal(autoDispatch, "max_session_usd")
                    ?? ReadDecimal(TryGetObject(root, "limits"), "max_session_usd"),
                MaxConcurrentSessionsCeiling = ReadInt(autoDispatch, "max_concurrent_sessions"),
                RequireApprovalAboveUsdCeiling = ReadDecimal(autoDispatch, "require_approval_above_usd"),
                PathAllowList = scopeAllow,
                ProjectTypeAllowList = ReadProjectTypes(autoDispatch),
            };
        }
    }

    /// <summary>The restriction implied by a repository row, snapshot and all.</summary>
    public static RepoAutoDispatchRestriction ReadAutoDispatchRestriction(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return ReadAutoDispatchRestriction(repo.CharterConfigSnapshot);
    }

    private static JsonElement? TryGetObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static IReadOnlyList<string>? ReadStringArray(JsonElement root, string parent, string name)
    {
        if (TryGetObject(root, parent) is not { } block
            || !block.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = array
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return values.Length == 0 ? null : values;
    }

    private static IReadOnlyList<ProjectType>? ReadProjectTypes(JsonElement? block)
    {
        if (block is not { } element
            || !element.TryGetProperty("project_types", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<ProjectType>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && Enum.TryParse<ProjectType>(item.GetString()?.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed))
            {
                values.Add(parsed);
            }
        }

        return values.Count == 0 ? null : values;
    }

    private static bool? ReadBool(JsonElement block, string name)
        => block.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static decimal? ReadDecimal(JsonElement? block, string name)
        => block is { } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDecimal(out var parsed)
            ? parsed
            : null;

    private static int? ReadInt(JsonElement? block, string name)
        => block is { } element
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

using System.Text.Json;
using Charter.Domain;
using Charter.Refinement;

namespace Charter.Orchestration;

/// <summary>
/// Builds the context refinement is grounded in from the repository row (sections 8, 10).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Onboarding.CharterFolder.ToRefinementContext"/> is the richer path and needs the
/// committed folder at a commit, which needs a provider call. This one reads the
/// <c>charter_config_snapshot</c> column that onboarding already wrote, which is what
/// <see cref="Auth.Authorization.RepoCharterConfig"/> reads for the same reason: a queue handler must
/// be able to do its work from Postgres alone (section 2.3), and a refinement turn that cannot start
/// because GitHub is slow is a refinement turn that fails for the wrong reason.
/// </para>
/// <para>
/// It fails closed. A repository with no snapshot, or an unreadable one, produces
/// <see cref="RefinementScopePolicy.DenyEverything"/> — and the refiner already refuses to spend a
/// token on a repository whose allow list is empty (section 7.3).
/// </para>
/// </remarks>
public static class RepoRefinementContext
{
    /// <summary>Projects a repository row into a refinement context.</summary>
    public static RefinementContext For(Repo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);

        if (string.IsNullOrWhiteSpace(repo.CharterConfigSnapshot))
        {
            return RefinementContext.Bare with { ProjectName = Humanise(repo.FullName) };
        }

        try
        {
            using var document = JsonDocument.Parse(repo.CharterConfigSnapshot);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return RefinementContext.Bare with { ProjectName = Humanise(repo.FullName) };
            }

            var root = document.RootElement;
            var project = Object(root, "project");

            return new RefinementContext
            {
                Scope = new RefinementScopePolicy(
                    Strings(Object(root, "scopes"), "allow"),
                    Strings(Object(root, "scopes"), "deny")),
                Glossary = Glossary(root),

                // Section 7.1: a requester never sees owner/repo, so the repository's own name is a
                // fallback for a project that did not give itself one, not the default.
                ProjectName = Text(project, "name")
                              ?? Text(root, "name")
                              ?? Humanise(repo.FullName),
                PrimerMarkdown = Text(project, "description") ?? Text(root, "description") ?? string.Empty,
            };
        }
        catch (JsonException)
        {
            // An unreadable snapshot must not widen anything. Deny everything and let refinement say
            // so in plain language rather than guessing at guardrails nobody could read.
            return RefinementContext.Bare with { ProjectName = Humanise(repo.FullName) };
        }
    }

    private static GlossaryDocument Glossary(JsonElement root)
    {
        if (Object(root, "glossary") is not { } block)
        {
            return GlossaryDocument.Empty;
        }

        var terms = new List<KeyValuePair<string, string>>();

        foreach (var property in block.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { Length: > 0 } definition)
            {
                terms.Add(new KeyValuePair<string, string>(property.Name, definition));
            }
        }

        return terms.Count == 0 ? GlossaryDocument.Empty : GlossaryDocument.From(terms);
    }

    private static JsonElement? Object(JsonElement? parent, string name)
        => parent is { } element
           && element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? Text(JsonElement? parent, string name)
        => parent is { } element
           && element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static IReadOnlyList<string> Strings(JsonElement? parent, string name)
    {
        if (parent is not { } element
            || !element.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value)),
        ];
    }

    private static string Humanise(string fullName)
    {
        var name = fullName.Contains('/', StringComparison.Ordinal)
            ? fullName[(fullName.LastIndexOf('/') + 1)..]
            : fullName;

        return name.Length == 0 ? "this project" : name.Replace('-', ' ').Replace('_', ' ');
    }
}

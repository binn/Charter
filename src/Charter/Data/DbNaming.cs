using System.Text;

namespace Charter.Data;

/// <summary>
/// The one place that decides what an identifier looks like in Postgres.
/// </summary>
/// <remarks>
/// Postgres folds unquoted identifiers to lower case, so a <c>PascalCase</c> model mapped verbatim
/// forces every hand-written query to quote every name. Charter maps to <c>snake_case</c> instead:
/// <c>CharterDbContext</c> applies this to every column, key, index and foreign key after the
/// entity configurations have run, so a raw statement in <see cref="JobQueue"/> and a psql session
/// spell things the same way.
/// </remarks>
internal static class DbNaming
{
    /// <summary>Converts a CLR-style identifier to <c>snake_case</c>. Idempotent.</summary>
    public static string ToSnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            if (char.IsUpper(current))
            {
                var previousIsBoundary = i > 0
                    && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));

                if (previousIsBoundary && builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}

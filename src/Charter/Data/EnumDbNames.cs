using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.Serialization;

namespace Charter.Data;

/// <summary>
/// The database spelling of an enum value.
/// </summary>
/// <remarks>
/// Every <c>status(...)</c> and <c>kind(...)</c> column in section 5 is stored as text, not as an
/// integer. An integer enum makes a migration a landmine — inserting a value in the middle silently
/// re-labels existing rows — and it makes <c>psql</c> output unreadable at exactly the moment
/// somebody is debugging a stuck session.
/// <para>
/// The default spelling is the value's name in <c>snake_case</c>. Where the specification writes a
/// value differently — <c>github-actions</c>, <c>rolling_30d</c>, <c>anthropic_oauth</c> — the value
/// carries an <see cref="EnumMemberAttribute"/> and that wins. <c>DomainEnumMappingTests</c> pins
/// every one of those strings against the specification.
/// </para>
/// </remarks>
internal static class EnumDbNames<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> ToDbNames = BuildToDb();
    private static readonly FrozenDictionary<string, TEnum> FromDbNames = BuildFromDb(ToDbNames);

    public static string ToDb(TEnum value)
        => ToDbNames.TryGetValue(value, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"{typeof(TEnum).Name} has no database spelling for this value.");

    public static TEnum FromDb(string name)
        => FromDbNames.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(name),
                name,
                $"'{name}' is not a known {typeof(TEnum).Name} value.");

    public static IReadOnlyDictionary<TEnum, string> All => ToDbNames;

    private static FrozenDictionary<TEnum, string> BuildToDb()
    {
        var map = new Dictionary<TEnum, string>();

        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var overridden = field.GetCustomAttribute<EnumMemberAttribute>()?.Value;
            map[value] = string.IsNullOrWhiteSpace(overridden)
                ? DbNaming.ToSnakeCase(field.Name)
                : overridden;
        }

        return map.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, TEnum> BuildFromDb(FrozenDictionary<TEnum, string> toDb)
    {
        var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);

        foreach (var (value, name) in toDb)
        {
            if (!map.TryAdd(name, value))
            {
                throw new InvalidOperationException(
                    $"{typeof(TEnum).Name} maps more than one value to the database spelling '{name}'.");
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

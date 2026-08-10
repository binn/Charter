using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Charter.Data;

/// <summary>Small shared spellings used by every entity configuration.</summary>
internal static class PropertyBuilderExtensions
{
    /// <summary>Postgres <c>jsonb</c>, held as JSON text so the domain owns no serialiser.</summary>
    public const string JsonColumnType = "jsonb";

    /// <summary>Money is <c>numeric</c>, never a float. Agent runs cost fractions of a cent.</summary>
    public const int MoneyPrecision = 14;

    public const int MoneyScale = 4;

    /// <summary>
    /// Stores an enum as <c>text</c> (see <see cref="EnumDbNames{TEnum}"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately unbounded rather than <c>varchar(n)</c> sized to the longest current value: in
    /// Postgres the two perform identically, and a length bound would turn adding a longer value to
    /// an enum into an <c>ALTER TABLE</c> on the largest tables in the schema.
    /// </remarks>
    public static PropertyBuilder<TEnum> HasEnumConversion<TEnum>(this PropertyBuilder<TEnum> builder)
        where TEnum : struct, Enum
        => builder.HasConversion(new EnumStringConverter<TEnum>());

    /// <summary>The nullable counterpart of <see cref="HasEnumConversion{TEnum}(PropertyBuilder{TEnum})"/>.</summary>
    public static PropertyBuilder<TEnum?> HasEnumConversion<TEnum>(this PropertyBuilder<TEnum?> builder)
        where TEnum : struct, Enum
        => builder.HasConversion(new EnumStringConverter<TEnum>());

    /// <summary>An element-wise enum conversion, producing a <c>text[]</c> column.</summary>
    public static PrimitiveCollectionBuilder<TCollection> HasEnumElements<TCollection, TEnum>(
        this PrimitiveCollectionBuilder<TCollection> builder)
        where TEnum : struct, Enum
        => builder.ElementType(element => element.HasConversion(new EnumStringConverter<TEnum>()));

    public static PropertyBuilder<string> IsJsonb(this PropertyBuilder<string> builder)
        => builder.HasColumnType(JsonColumnType);

    public static PropertyBuilder<string?> IsOptionalJsonb(this PropertyBuilder<string?> builder)
        => builder.HasColumnType(JsonColumnType);

    public static PropertyBuilder<decimal> IsMoney(this PropertyBuilder<decimal> builder)
        => builder.HasPrecision(MoneyPrecision, MoneyScale);

    public static PropertyBuilder<decimal?> IsMoney(this PropertyBuilder<decimal?> builder)
        => builder.HasPrecision(MoneyPrecision, MoneyScale);
}

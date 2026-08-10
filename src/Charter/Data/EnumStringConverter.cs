using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Charter.Data;

/// <summary>
/// Stores an enum as its <see cref="EnumDbNames{TEnum}"/> spelling rather than as an integer.
/// </summary>
internal sealed class EnumStringConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public EnumStringConverter()
        : base(value => EnumDbNames<TEnum>.ToDb(value), name => EnumDbNames<TEnum>.FromDb(name))
    {
    }
}

using System.Data;
using Dapper;

namespace EmailTriage.Core.Data;

/// <summary>
/// SQLite has no date type. We store ISO-8601 round-trip strings so ordering is
/// lexicographic and correct, and teach Dapper to read them back.
/// </summary>
public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public static readonly DateTimeOffsetHandler Instance = new();

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value.ToUniversalTime().ToString("O");

    public override DateTimeOffset Parse(object value) => value switch
    {
        string s => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        DateTimeOffset dto => dto,
        _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset."),
    };
}

public static class SqlMapping
{
    private static bool _registered;
    private static readonly object Gate = new();

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            // Dapper registers Nullable<DateTimeOffset> alongside this one,
            // so nullable columns are covered by the same handler.
            SqlMapper.AddTypeHandler(DateTimeOffsetHandler.Instance);
            _registered = true;
        }
    }
}

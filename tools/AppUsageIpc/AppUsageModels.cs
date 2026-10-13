using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppUsageIpc;

[JsonConverter(typeof(ApprovedEnumJsonConverter<AppUsageRange>))]
public enum AppUsageRange { Today = 0, SevenDays = 7, ThirtyDays = 30 }

[JsonConverter(typeof(ApprovedEnumJsonConverter<AppUsageAvailability>))]
public enum AppUsageAvailability { Available, Unavailable, Degraded }

public sealed record AppUsageQueryRequest(AppUsageRange Range);

public sealed record AppUsageApplication(string ApplicationIdentity, string DisplayName,
    string? ExecutableName, string? ExecutablePath, long DownloadBytes, long UploadBytes,
    long TotalBytes, bool IsSystem, bool IsUnknown);

public sealed record AppUsageQueryResult(AppUsageRange Range, DateOnly StartLocalDate,
    DateOnly EndLocalDate, int TrackedApplicationCount, long TotalDownloadBytes,
    long TotalUploadBytes, long LogicalTotalBytes, IReadOnlyList<AppUsageApplication> Applications,
    AppUsageAvailability Availability = AppUsageAvailability.Available, string? Diagnostic = null);

public static class AppUsageRangeRules
{
    public static DateOnly GetStartLocalDate(AppUsageRange range, DateOnly endDate)
    {
        int days = range switch
        {
            AppUsageRange.Today => 1,
            AppUsageRange.SevenDays => 7,
            AppUsageRange.ThirtyDays => 30,
            _ => throw new ArgumentOutOfRangeException(nameof(range), "Only Today, 7D and 30D are supported.")
        };
        return DateOnly.FromDayNumber(Math.Max(DateOnly.MinValue.DayNumber, endDate.DayNumber - days + 1));
    }

    public static AppUsageQueryResult Unavailable(AppUsageRange range)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return new(range, GetStartLocalDate(range, today), today, 0, 0, 0, 0,
            Array.Empty<AppUsageApplication>(), AppUsageAvailability.Unavailable,
            "Usage history temporarily unavailable.");
    }
}

// Numeric values, numeric strings, case variants and undefined enum values are not wire inputs.
public sealed class ApprovedEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException();
        var name = reader.GetString();
        if (!Enum.TryParse<T>(name, ignoreCase: false, out var value) || Enum.GetName(value) != name)
            throw new JsonException();
        return value;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var name = Enum.GetName(value) ?? throw new JsonException();
        writer.WriteStringValue(name);
    }
}

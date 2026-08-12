using System.Globalization;
using System.Text.Json;

namespace UsageMonitor.Core.Providers;

internal static class ProviderJson
{
    public static JsonDocument? Parse(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    public static JsonElement? Property(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        return null;
    }

    public static string? String(JsonElement? value)
    {
        if (value is not { } item) return null;
        return item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    }

    public static double? Number(JsonElement? value)
    {
        if (value is not { } item) return null;
        double number;
        if (item.ValueKind == JsonValueKind.Number)
        {
            if (!item.TryGetDouble(out number)) return null;
        }
        else if (item.ValueKind == JsonValueKind.String)
        {
            if (!double.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
        }
        else return null;
        // double.TryParse accepts "NaN"/"Infinity"/"1e999" strings; a corrupt provider field must
        // not poison day totals or persist into the history index as a non-finite value.
        return double.IsFinite(number) ? number : null;
    }

    /// <summary>Number lookup with a zero default, shared by JSONL usage scanners.</summary>
    public static double NumberOrZero(JsonElement element, params string[] names)
        => Number(Property(element, names)) ?? 0;

    public static bool? Bool(JsonElement? value)
    {
        if (value is not { } item) return null;
        return item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : null;
    }

    public static DateTimeOffset? Date(JsonElement? value)
    {
        if (value is not { } item) return null;
        if (item.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(item.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date.ToUniversalTime();
        if (Number(item) is { } number)
        {
            var seconds = Math.Abs(number) > 100_000_000_000 ? number / 1000 : number;
            try { return DateTimeOffset.FromUnixTimeSeconds((long)seconds); } catch (ArgumentOutOfRangeException) { }
        }
        return null;
    }

    public static JsonElement? Object(JsonElement? value) => value is { ValueKind: JsonValueKind.Object } ? value : null;
    public static JsonElement? Array(JsonElement? value) => value is { ValueKind: JsonValueKind.Array } ? value : null;
}

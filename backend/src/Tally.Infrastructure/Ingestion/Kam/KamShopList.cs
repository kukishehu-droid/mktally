using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Kam;

internal sealed record KamShopFile(string RelativePath, DateTimeOffset? UploadedAt);

internal sealed record KamShop
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }
    public string? Municipality { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Phone { get; init; }
    public string? WorkingHours { get; init; }
    public string? SundayWorkingHours { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<KamShopFile> Files { get; init; } = [];

    /// <summary>The newest file. Every store had exactly one on 2026-11-22.</summary>
    public KamShopFile? CurrentFile =>
        Files.OrderByDescending(f => f.UploadedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(f => f.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault();
}

/// <summary>
/// <c>POST /ShopsWeb/LoadShopList</c> → stores. Pure. Shape verified 2026-11-22 (REQUIREMENTS.md
/// §4.2): a JSON array; <c>Coordinates</c> is "lat, lon" in one string; <c>DateUploaded</c> is
/// the ASP.NET form <c>"\/Date(1795302000000)\/"</c> (midnight Skopje time of the upload day).
/// Fields are read leniently: one malformed store is skipped, not the whole list.
/// </summary>
internal static partial class KamShopList
{
    // Rough North Macedonia bounding box. A coordinate outside it is a data-entry slip.
    private const double MinLat = 40.8, MaxLat = 42.4, MinLon = 20.4, MaxLon = 23.1;

    /// <exception cref="KamFormatException">Not a JSON array.</exception>
    public static IReadOnlyList<KamShop> Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            var preview = json.Length > 120 ? json[..120] + "…" : json;
            throw new KamFormatException($"Store list is not JSON: {preview}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new KamFormatException($"Store list is a JSON {doc.RootElement.ValueKind}, expected an array.");
            }

            var shops = new List<KamShop>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Int(e, "Id") is not { } id)
                {
                    continue;
                }

                var (lat, lon) = ParseCoordinates(Str(e, "Coordinates"));
                shops.Add(new KamShop
                {
                    Id = id,
                    // Live names carry trailing spaces ("Генц ").
                    Name = Clean(Str(e, "Name")) ?? $"КАМ {id}",
                    Address = Clean(Str(e, "Address")),
                    City = Clean(Str(e, "City")),
                    Municipality = Clean(Str(e, "Municipality")),
                    Latitude = lat,
                    Longitude = lon,
                    Phone = Clean(Str(e, "ContactPhone")),
                    WorkingHours = Clean(Str(e, "WorkingHours")),
                    SundayWorkingHours = Clean(Str(e, "SundayWorkingHours")),
                    Tags = Strings(e, "Tags"),
                    Files = Files(e),
                });
            }
            return shops;
        }
    }

    internal static (double? Lat, double? Lon) ParseCoordinates(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || lat is < MinLat or > MaxLat
            || lon is < MinLon or > MaxLon)
        {
            return (null, null);
        }
        return (lat, lon);
    }

    /// <summary><c>/Date(1795302000000)/</c>, optionally with a <c>+0100</c> suffix → instant.</summary>
    internal static DateTimeOffset? ParseAspNetDate(string? text)
    {
        var m = text is null ? Match.Empty : AspNetDateRegex().Match(text);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
        {
            return null;
        }
        try
        {
            // The suffix, when present, only says how to display it; the milliseconds are UTC.
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static List<KamShopFile> Files(JsonElement shop)
    {
        var files = new List<KamShopFile>();
        if (!shop.TryGetProperty("ShopFiles", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return files;
        }
        foreach (var f in arr.EnumerateArray())
        {
            if (f.ValueKind == JsonValueKind.Object && Clean(Str(f, "RelativePath")) is { } path)
            {
                files.Add(new KamShopFile(path, ParseAspNetDate(Str(f, "DateUploaded"))));
            }
        }
        return files;
    }

    private static List<string> Strings(JsonElement e, string property) =>
        e.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString()!.Trim())
                .Where(t => t.Length > 0)
                .ToList()
            : [];

    private static string? Clean(string? text) => MandatedFieldParser.CleanText(text);

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }
        return v.ValueKind == JsonValueKind.String
               && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? s
            : null;
    }

    [GeneratedRegex(@"/Date\((-?\d+)(?:[+-]\d{4})?\)/")]
    private static partial Regex AspNetDateRegex();
}

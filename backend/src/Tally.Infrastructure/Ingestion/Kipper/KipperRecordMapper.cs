using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Kipper;

/// <summary>
/// One DataTables row → <see cref="RawPriceRecord"/>. Pure; no I/O. Field names are the
/// verified ones from REQUIREMENTS.md §4.1. Values are read leniently because DataTables
/// back ends send numbers as strings or numbers, and empty as null or "".
/// </summary>
internal static partial class KipperRecordMapper
{
    public static bool TryMap(JsonElement row, DateOnly? publishedOn, out RawPriceRecord record, out string? skipReason)
    {
        record = null!;

        if (row.ValueKind != JsonValueKind.Object)
        {
            skipReason = $"row is {row.ValueKind}, expected an object";
            return false;
        }

        var name = MandatedFieldParser.CleanText(Str(row, "product_name"));
        if (name is null)
        {
            skipReason = "missing product_name";
            return false;
        }

        var price = MandatedFieldParser.ParseDenars(Str(row, "product_price"));
        if (price is null or <= 0)
        {
            skipReason = $"unparseable product_price '{Str(row, "product_price")}'";
            return false;
        }

        var regular = MandatedFieldParser.ParseDenars(Str(row, "product_price_normal"));
        var pct = MandatedFieldParser.ParsePercent(Str(row, "product_price_discount_percentage"));

        // Kipper has no separate promo-price field: a promotion is a selling price below
        // the normal price. Flag it, because promos expire (CLAUDE.md).
        int? promo = regular is not null && price < regular ? price : null;
        if (promo is not null && (pct is null || pct == 0m) && regular is > 0)
        {
            pct = Math.Round((regular!.Value - promo.Value) * 100m / regular.Value, 2);
        }

        // "product_price_weight": "600" + "product_type_alt": "1КГ=" → 600 den per 1 kg.
        var unit = ParseUnit(Str(row, "product_type_alt"), Str(row, "product_price_weight"));

        record = new RawPriceRecord
        {
            SourceKey = SourceKeyFor(Str(row, "product_image"), name),
            NamePrimary = name,
            NameAlt = MandatedFieldParser.CleanText(Str(row, "product_name_alt")), // Albanian
            Category = MandatedFieldParser.CleanText(Str(row, "product_subgroup")),
            Price = price.Value,
            RegularPrice = regular,
            PromoPrice = promo,
            DiscountPct = promo is null ? null : pct,
            UnitPrice = unit?.Price,
            UnitBasis = unit?.Basis,
            IsAvailable = null, // Kipper does not publish availability (REQUIREMENTS.md §4.1)
            PromoType = null,
            PromoFrom = promo is null ? null
                : MandatedFieldParser.ParseLocalInstant(Str(row, "promotion_datetime_from"), endOfDayIfDateOnly: false),
            PromoTo = promo is null ? null
                : MandatedFieldParser.ParseLocalInstant(Str(row, "promotion_datetime_to"), endOfDayIfDateOnly: true),
            PublishedOn = publishedOn,
        };
        skipReason = null;
        return true;
    }

    internal static (decimal Price, string Basis)? ParseUnit(string? typeAlt, string? weightPrice)
    {
        var type = MandatedFieldParser.CleanText(typeAlt);
        var value = MandatedFieldParser.CleanText(weightPrice);
        if (type is null || value is null)
        {
            return null;
        }
        // Live data has "=1Л" and "=1КГ" alongside the usual "1Л=" (2026-11-22).
        if (type.StartsWith('='))
        {
            type = type[1..].Trim() + "=";
        }
        return MandatedFieldParser.ParseUnitPrice(type.Contains('=') ? type + value : type + "=" + value);
    }

    /// <summary>
    /// No EAN in the payload. <c>product_image</c> looks like the article number
    /// ("000307.webp"), which is a far better key than the name; use it when the file
    /// stem is all digits, otherwise fall back to the name hash. Assumption to confirm
    /// against live data: one image stem per article, stable over time.
    /// </summary>
    internal static string SourceKeyFor(string? image, string name)
    {
        var stem = image is null ? null : Path.GetFileNameWithoutExtension(image.Trim());
        return stem is not null && ArticleCodeRegex().IsMatch(stem)
            ? SourceKeys.ForCode(stem)
            : SourceKeys.ForName(name);
    }

    private static string? Str(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    [GeneratedRegex(@"^\d{3,}$")]
    private static partial Regex ArticleCodeRegex();
}

/// <summary>Response envelope of a DataTables server-side request.</summary>
internal sealed record KipperDataPage(int? Draw, int? RecordsTotal, int? RecordsFiltered, IReadOnlyList<JsonElement> Rows)
{
    /// <exception cref="KipperFormatException">Not a DataTables response.</exception>
    public static KipperDataPage Parse(string body)
    {
        var trimmed = body.Trim();

        // admin-ajax.php's own failure answers.
        if (trimmed == "0")
        {
            throw new KipperFormatException("admin-ajax.php returned 0: the action is not registered. Check Crawlers:Kipper:AjaxAction.");
        }
        if (trimmed == "-1")
        {
            throw new KipperFormatException("admin-ajax.php returned -1: a nonce or permission check failed. The endpoint needed none on 2026-11-22; the site may have added one (Crawlers:Kipper:ExtraFormFields).");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException ex)
        {
            var preview = trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
            throw new KipperFormatException($"AJAX response is not JSON: {preview}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new KipperFormatException("AJAX response has no 'data' array.");
            }

            // Clone so rows outlive the document.
            var rows = data.EnumerateArray().Select(r => r.Clone()).ToList();
            return new KipperDataPage(Int(root, "draw"), Int(root, "recordsTotal"), Int(root, "recordsFiltered"), rows);
        }
    }

    private static int? Int(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }
        if (v.ValueKind == JsonValueKind.String
            && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            return s;
        }
        return null;
    }
}

public sealed class KipperFormatException(string message, Exception? inner = null) : Exception(message, inner);

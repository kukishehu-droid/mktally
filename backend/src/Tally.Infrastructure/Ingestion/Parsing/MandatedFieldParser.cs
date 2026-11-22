using System.Globalization;
using System.Text.RegularExpressions;

namespace Tally.Infrastructure.Ingestion.Parsing;

/// <summary>
/// Parsers for the cell formats of the legally mandated price-list columns
/// (REQUIREMENTS.md §3, §4.2, §4.3). KAM's PDF and Misa's HTML publish the same column
/// set with the same conventions, so both adapters share these.
/// </summary>
internal static partial class MandatedFieldParser
{
    /// <summary>
    /// "210 ден." → 210. "1.250 ден." → 1250 (dot as thousands separator).
    /// "89,50 ден." → 90 (rounded; DATA-MODEL.md §1 stores whole denars).
    /// Returns null for empty or non-numeric cells.
    /// </summary>
    public static int? ParseDenars(string? text)
    {
        var value = ParseNumber(text);
        return value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    /// <summary>"-20%", "20 %", "20" → 20. Sign is dropped: a discount is always a reduction.</summary>
    public static decimal? ParsePercent(string? text)
    {
        var value = ParseNumber(text);
        return value is null ? null : Math.Abs(value.Value);
    }

    /// <summary>
    /// A cell holding a price, a percentage, or both. Misa's live "Цена со попуст" cell is
    /// "299 ден. Попуст:3%". The amount is the number before "ден"; the percentage is the
    /// number before "%". A bare number with neither marker is read as denars.
    /// </summary>
    public static (int? Denars, decimal? Percent) ParsePriceAndPercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var t = Normalise(text);
        var pctMatch = PercentRegex().Match(t);
        decimal? pct = pctMatch.Success ? ParsePercent(pctMatch.Groups[1].Value) : null;

        var denMatch = DenarAmountRegex().Match(t);
        int? denars = denMatch.Success
            ? ParseDenars(denMatch.Groups[1].Value)
            : pctMatch.Success ? null : ParseDenars(t);

        return (denars, pct);
    }

    /// <summary>"Да" → true, "Не" → false, anything else → null.</summary>
    public static bool? ParseAvailability(string? text)
    {
        var t = text?.Trim().ToLowerInvariant();
        return t switch
        {
            "да" or "da" or "yes" => true,
            "не" or "ne" or "no" => false,
            _ => null,
        };
    }

    /// <summary>
    /// Unit price as published: "100 гр = 31.78 ден." → (31.78, "100G"); "1 кг = 600 ден." →
    /// (600, "1KG"); "1КГ=600" → (600, "1KG"). Returns null when empty or when the unit is not
    /// recognised, because a unit price without a trustworthy basis is worse than none.
    /// </summary>
    public static (decimal Price, string Basis)? ParseUnitPrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = UnitPriceRegex().Match(Normalise(text));
        if (!m.Success)
        {
            return null;
        }

        var unit = CanonicalUnit(m.Groups["u"].Value);
        var price = ParseNumber(m.Groups["p"].Value);
        if (unit is null || price is null)
        {
            return null;
        }

        var qty = m.Groups["q"].Success ? ParseNumber(m.Groups["q"].Value) : 1m;
        if (qty is null or <= 0)
        {
            return null;
        }

        return (price.Value, $"{qty.Value.ToString("0.###", CultureInfo.InvariantCulture)}{unit}");
    }

    /// <summary>
    /// Promotion window, e.g. "01.11.2026 - 30.11.2026", "од 01.11.2026 до 30.11.2026 23:59".
    /// Dates are Skopje local time; a date without a time means start of day for "from" and
    /// end of day for "to". A single date preceded by "до" is an end date, otherwise a start date.
    /// </summary>
    public static (DateTimeOffset? From, DateTimeOffset? To) ParsePromoWindow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var normalised = Normalise(text);
        var matches = DateRegex().Matches(normalised);
        if (matches.Count == 0)
        {
            return (null, null);
        }

        if (matches.Count == 1)
        {
            var only = matches[0];
            var isEnd = normalised[..only.Index].TrimEnd().EndsWith("до", StringComparison.OrdinalIgnoreCase);
            var value = ToInstant(only, endOfDay: isEnd);
            return isEnd ? (null, value) : (value, null);
        }

        return (ToInstant(matches[0], endOfDay: false), ToInstant(matches[1], endOfDay: true));
    }

    /// <summary>
    /// A single Skopje-local date or date-time ("2026-11-20 00:00:00", "20.11.2026 08:00")
    /// → UTC. A date without a time is start of day, or end of day when
    /// <paramref name="endOfDayIfDateOnly"/> is set (for "valid until" values).
    /// </summary>
    public static DateTimeOffset? ParseLocalInstant(string? text, bool endOfDayIfDateOnly)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var m = DateRegex().Match(Normalise(text));
        return m.Success ? ToInstant(m, endOfDayIfDateOnly) : null;
    }

    /// <summary>Collapse whitespace (including NBSP) and trim. Null and blank become null.</summary>
    public static string? CleanText(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var cleaned = Normalise(text);
        return cleaned.Length == 0 ? null : cleaned;
    }

    internal static decimal? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = NumberRegex().Match(Normalise(text).Replace(" ", ""));
        if (!m.Success)
        {
            return null;
        }

        var raw = m.Value;

        // 1.250 / 1,250 / 12.500.000 → thousands grouping. A three-digit fraction is not
        // plausible for denar prices, so treat exact 3-digit groups as grouping.
        if (ThousandsRegex().IsMatch(raw))
        {
            raw = raw.Replace(".", "").Replace(",", "");
        }
        else
        {
            raw = raw.Replace(',', '.');
        }

        return decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static string? CanonicalUnit(string token)
    {
        var t = token.Trim().TrimEnd('.').ToLowerInvariant();
        return t switch
        {
            "г" or "гр" or "грам" or "g" or "gr" or "gram" => "G",
            "кг" or "kg" => "KG",
            "мл" or "ml" => "ML",
            "л" or "лит" or "литар" or "l" or "lit" => "L",
            "ком" or "парче" or "пар" or "pc" or "pcs" or "kom" or "copë" => "PC",
            _ => null,
        };
    }

    // Null for impossible dates ("31.02.2026") so one bad cell skips a field, not the whole run.
    private static DateTimeOffset? ToInstant(Match m, bool endOfDay)
    {
        int day, month, year;
        if (m.Groups["iy"].Success)
        {
            year = int.Parse(m.Groups["iy"].Value, CultureInfo.InvariantCulture);
            month = int.Parse(m.Groups["im"].Value, CultureInfo.InvariantCulture);
            day = int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        }

        if (year is < 2000 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        TimeOnly time;
        if (m.Groups["hh"].Success)
        {
            var hh = int.Parse(m.Groups["hh"].Value, CultureInfo.InvariantCulture);
            var mm = int.Parse(m.Groups["mm"].Value, CultureInfo.InvariantCulture);
            if (hh > 23 || mm > 59)
            {
                return null;
            }
            time = new TimeOnly(hh, mm);
        }
        else
        {
            time = endOfDay ? new TimeOnly(23, 59, 59) : TimeOnly.MinValue;
        }

        return SkopjeTime.ToUtc(new DateOnly(year, month, day), time);
    }

    private static string Normalise(string text) =>
        WhitespaceRegex().Replace(text.Replace(' ', ' '), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"(\d[\d.,\s]*?)\s*ден", RegexOptions.IgnoreCase)]
    private static partial Regex DenarAmountRegex();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^\d{1,3}(?:[.,]\d{3})+$")]
    private static partial Regex ThousandsRegex();

    // "<qty?> <unit> = <price>", qty optional ("КГ=600").
    [GeneratedRegex(@"^\s*(?<q>\d+(?:[.,]\d+)?)?\s*(?<u>[^\d\s=]+?)\.?\s*=\s*(?<p>\d+(?:[.,]\d+)?)")]
    private static partial Regex UnitPriceRegex();

    [GeneratedRegex(@"(?:(?<d>\d{1,2})[./-](?<m>\d{1,2})[./-](?<y>\d{4})|(?<iy>\d{4})-(?<im>\d{2})-(?<id>\d{2}))(?:[ T]+(?<hh>\d{1,2}):(?<mm>\d{2})(?::\d{2})?)?")]
    private static partial Regex DateRegex();
}

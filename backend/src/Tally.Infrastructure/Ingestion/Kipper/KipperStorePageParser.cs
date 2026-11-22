using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Kipper;

/// <summary>What the store page tells us. Every field is optional.</summary>
/// <param name="PostId">WordPress post id of the page, sent as the AJAX store parameter.</param>
internal sealed record KipperStorePage(
    DateTimeOffset? LastPriceUpdate,
    double? Latitude,
    double? Longitude,
    string? PostId);

/// <summary>
/// Pure HTML → <see cref="KipperStorePage"/>. Verified against the live store pages on
/// 2026-11-22: the timestamp is server-rendered as
/// "Датум и време на последно ажурирање на цените: 20 November 2026 - 15:43", the map link
/// is <c>/maps/@lat,lon</c> or <c>/maps/dir//lat,lon/@viewport</c>, and the body class
/// carries <c>postid-NNNNN</c>.
/// </summary>
internal static partial class KipperStorePageParser
{
    // Rough North Macedonia bounding box. A coordinate outside it is a parsing mistake.
    private const double MinLat = 40.8, MaxLat = 42.4, MinLon = 20.4, MaxLon = 23.1;

    public static KipperStorePage Parse(string html)
    {
        using var doc = new HtmlParser().ParseDocument(html);

        var text = MandatedFieldParser.CleanText(doc.Body?.TextContent) ?? "";
        var links = doc.QuerySelectorAll("a[href], iframe[src]")
            .Select(e => e.GetAttribute("href") ?? e.GetAttribute("src") ?? "");
        var (lat, lon) = FindCoordinates(links);

        var postId = PostIdClassRegex().Match(doc.Body?.ClassName ?? "") is { Success: true } m
            ? m.Groups[1].Value
            : ShortlinkRegex().Match(html) is { Success: true } s ? s.Groups[1].Value : null;

        return new KipperStorePage(FindLastUpdate(text), lat, lon, postId);
    }

    internal static DateTimeOffset? FindLastUpdate(string text)
    {
        var label = UpdateLabelRegex().Match(text);
        if (!label.Success)
        {
            return null;
        }
        var start = label.Index + label.Length;
        var after = text.Substring(start, Math.Min(80, text.Length - start));
        return ParseNamedMonth(after) ?? MandatedFieldParser.ParseLocalInstant(after, endOfDayIfDateOnly: false);
    }

    /// <summary>"20 November 2026 - 15:43", "22 ноември 2026 06:15", "5 nëntor 2026" (Skopje local) → UTC.</summary>
    internal static DateTimeOffset? ParseNamedMonth(string text)
    {
        var m = NamedMonthRegex().Match(text);
        if (!m.Success || Month(m.Groups["mon"].Value) is not { } month)
        {
            return null;
        }

        var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var time = TimeOnly.MinValue;
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

        return SkopjeTime.ToUtc(new DateOnly(year, month, day), time);
    }

    // English, Macedonian, Albanian. Prefixes, longest first, so "shk" (Feb) and "sht" (Sep) differ.
    private static readonly (string Prefix, int Month)[] MonthPrefixes =
    [
        ("january", 1), ("february", 2), ("march", 3), ("april", 4), ("may", 5), ("june", 6),
        ("july", 7), ("august", 8), ("september", 9), ("october", 10), ("november", 11), ("december", 12),
        ("јан", 1), ("фев", 2), ("мар", 3), ("апр", 4), ("мај", 5), ("јун", 6),
        ("јул", 7), ("авг", 8), ("сеп", 9), ("окт", 10), ("ное", 11), ("дек", 12),
        ("janar", 1), ("shkurt", 2), ("mars", 3), ("prill", 4), ("maj", 5), ("qershor", 6),
        ("korrik", 7), ("gusht", 8), ("shtator", 9), ("tetor", 10), ("nëntor", 11), ("nentor", 11), ("dhjetor", 12),
        ("jan", 1), ("feb", 2), ("mar", 3), ("apr", 4), ("jun", 6), ("jul", 7), ("aug", 8),
        ("sep", 9), ("oct", 10), ("nov", 11), ("dec", 12),
    ];

    private static int? Month(string word)
    {
        var w = word.ToLowerInvariant();
        foreach (var (prefix, month) in MonthPrefixes.OrderByDescending(p => p.Prefix.Length))
        {
            if (prefix.StartsWith(w, StringComparison.Ordinal) && w.Length >= 3
                || w.StartsWith(prefix, StringComparison.Ordinal))
            {
                return month;
            }
        }
        return null;
    }

    internal static (double? Lat, double? Lon) FindCoordinates(IEnumerable<string> urls)
    {
        foreach (var raw in urls)
        {
            if (!raw.Contains("google", StringComparison.OrdinalIgnoreCase)
                && !raw.Contains("goo.gl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = Uri.UnescapeDataString(raw);
            // Directions destination first: in "/dir//41.19,20.67/@41.190,20.675" the part
            // after "@" is only where the map view is centred.
            foreach (var regex in new[] { DirCoordsRegex(), QueryCoordsRegex(), BangCoordsRegex(), AtCoordsRegex() })
            {
                var m = regex.Match(url);
                if (m.Success
                    && double.TryParse(m.Groups["lat"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    && double.TryParse(m.Groups["lon"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                    && lat is >= MinLat and <= MaxLat
                    && lon is >= MinLon and <= MaxLon)
                {
                    return (lat, lon);
                }
            }
        }
        return (null, null);
    }

    // "Датум и време на последно ажурирање на цените:" and variants, MK and SQ.
    [GeneratedRegex(@"(последн\w*\s+(ажурир\w*|промен\w*|измен\w*)(\s+на\s+цени\w*)?\s*:?|ажурирано\s*:?|përditësim\w*\s*:?|perditesim\w*\s*:?)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateLabelRegex();

    [GeneratedRegex(@"(?<d>\d{1,2})\.?\s+(?<mon>\p{L}{3,})\.?\s+(?<y>\d{4})(?:\s*[-–,]?\s*(?<hh>\d{1,2}):(?<mm>\d{2}))?")]
    private static partial Regex NamedMonthRegex();

    [GeneratedRegex(@"postid-(\d+)")]
    private static partial Regex PostIdClassRegex();

    [GeneratedRegex(@"rel=['""]shortlink['""][^>]*[?&]p=(\d+)")]
    private static partial Regex ShortlinkRegex();

    [GeneratedRegex(@"/dir/[^/]*/(?<lat>-?\d{1,2}\.\d+),\s*(?<lon>-?\d{1,3}\.\d+)")]
    private static partial Regex DirCoordsRegex();

    [GeneratedRegex(@"@(?<lat>-?\d{1,2}\.\d+),(?<lon>-?\d{1,3}\.\d+)")]
    private static partial Regex AtCoordsRegex();

    [GeneratedRegex(@"[?&](?:q|ll|query|destination|center|daddr)=(?:loc:)?(?<lat>-?\d{1,2}\.\d+),\s*(?<lon>-?\d{1,3}\.\d+)")]
    private static partial Regex QueryCoordsRegex();

    [GeneratedRegex(@"!3d(?<lat>-?\d{1,2}\.\d+)!4d(?<lon>-?\d{1,3}\.\d+)")]
    private static partial Regex BangCoordsRegex();
}

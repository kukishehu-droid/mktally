using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Kam;

/// <summary>
/// One drawn character, in PDF user space (points, origin bottom left, y grows upward).
/// </summary>
/// <param name="X">Where the glyph starts on its baseline.</param>
/// <param name="EndX">Where the next glyph would start.</param>
/// <param name="BaselineY">Baseline height. Glyphs on one text line share it.</param>
internal readonly record struct KamGlyph(string Value, double X, double EndX, double BaselineY, double PointSize)
{
    public double CenterX => (X + EndX) / 2;
    public bool IsBlank => string.IsNullOrWhiteSpace(Value);
}

/// <summary>The nine mandated columns, in KAM's order (REQUIREMENTS.md §4.2).</summary>
internal enum KamColumn
{
    Name = 0,            // Назив на стока - производ
    Price = 1,           // Продажна цена
    UnitPrice = 2,       // Единична цена
    Description = 3,     // Опис на стока
    Availability = 4,    // Достапност во продажен објект
    RegularPrice = 5,    // Редовна цена
    DiscountedPrice = 6, // Цена со попуст, with "Попуст(%)" beneath in the same cell
    PromoType = 7,       // Вид на продажно поттикнување
    PromoPeriod = 8,     // Времетраење на промоција или попуст
}

internal sealed record KamRow(int Page, IReadOnlyList<string?> Cells)
{
    public string? this[KamColumn c] => Cells[(int)c];
}

/// <param name="SnapshotAt">
/// The "Датум и време на последно ажурирање на цените" stamp above the table, as UTC. This, not
/// the URL date, is when the prices are from: on 2026-11-22 the file at <c>2026/11/22/38.pdf</c>
/// said 18.11.2026.
/// </param>
/// <param name="OrphanGlyphs">Non-blank glyphs below the header that fit no row or column.</param>
internal sealed record KamTable(
    IReadOnlyList<KamRow> Rows,
    DateTimeOffset? SnapshotAt,
    string? SnapshotText,
    int Pages,
    int OrphanGlyphs,
    int TableGlyphs);

internal sealed record KamTableLayout(IReadOnlyList<double> ColumnEdges, double RowRise, double RowMaxDepth)
{
    /// <summary>
    /// The vertical rules of the Crystal Reports table, measured from the live Struga PDFs on
    /// 2026-11-22. Identical on every page and in every store's file.
    /// </summary>
    public static IReadOnlyList<double> DefaultColumnEdges { get; } =
        [6, 84.75, 166.5, 254.25, 336.75, 430.5, 522.75, 602, 699, 800.25];

    /// <summary>
    /// Rows are a fixed 37.45 pt tall and cells are top-aligned. Measured against each row's
    /// selling-price baseline, every glyph of the row sat between +2 pt (a smaller first
    /// line of a wrapped name) and -18.6 pt (the third line of a description). Rise and depth
    /// leave margin on both sides and still stay clear of the neighbouring rows.
    /// </summary>
    public static KamTableLayout Default { get; } = new(DefaultColumnEdges, RowRise: 6, RowMaxDepth: 30);
}

/// <summary>
/// Glyphs → table rows, by position. Pure; no PDF library here, so it is testable on
/// hand-built glyphs. REQUIREMENTS.md §4.2: naive text extraction interleaves the columns.
/// </summary>
/// <remarks>
/// <para>Rows: every product row has exactly one selling price, drawn as one line
/// ("102ден.") in the price column. Those baselines are the row anchors. A glyph belongs to
/// the highest-sitting anchor that is no more than <see cref="KamTableLayout.RowRise"/>
/// below it, provided it is within <see cref="KamTableLayout.RowMaxDepth"/> of it.
/// Anything above the first anchor is the page header.</para>
/// <para>Columns: by the glyph's centre against the fixed vertical rules. Cell text is
/// centred, so centres sit well inside their column.</para>
/// <para>Cells wrap onto up to three lines; wrapped lines are joined with a space. Checked
/// on all 5,825 wrapped cell lines of store 38: every break is between words.</para>
/// </remarks>
internal static partial class KamTableReader
{
    private const double SameBaseline = 0.5;

    /// <summary>A word gap: wider than this share of the font size, with no space glyph drawn.</summary>
    private const double GapFactor = 0.15;

    // One label word per column that must sit in that column of the header.
    private static readonly string[] HeaderLabels =
        ["назив", "продажна", "единична", "опис", "достапност", "редовна", "попуст", "вид", "времетраење"];

    /// <exception cref="KamFormatException">The header does not match the expected columns.</exception>
    public static KamTable Read(IReadOnlyList<IReadOnlyList<KamGlyph>> pages, KamTableLayout layout)
    {
        if (layout.ColumnEdges.Count != HeaderLabels.Length + 1)
        {
            throw new ArgumentException($"Expected {HeaderLabels.Length + 1} column edges.", nameof(layout));
        }

        var rows = new List<KamRow>();
        DateTimeOffset? snapshotAt = null;
        string? snapshotText = null;
        var orphans = 0;
        var tableGlyphs = 0;
        var headerChecked = false;

        for (var p = 0; p < pages.Count; p++)
        {
            var glyphs = pages[p];
            if (glyphs.Count == 0)
            {
                continue;
            }

            var anchors = FindAnchors(glyphs, layout); // ascending
            var headerFloor = anchors.Count > 0 ? anchors[^1] + layout.RowRise : double.NegativeInfinity;
            var header = glyphs.Where(g => g.BaselineY > headerFloor).ToList();

            if (!headerChecked && header.Count > 0)
            {
                CheckHeader(header, layout, p + 1);
                (snapshotAt, snapshotText) = FindSnapshot(header);
                headerChecked = true;
            }

            if (anchors.Count == 0)
            {
                // A page with only the header: KAM's empty table (store 69 on 2026-11-22).
                continue;
            }

            var cells = new List<KamGlyph>?[anchors.Count, HeaderLabels.Length];
            foreach (var g in glyphs)
            {
                if (g.BaselineY > headerFloor)
                {
                    continue;
                }
                if (!g.IsBlank)
                {
                    tableGlyphs++;
                }

                var row = AnchorFor(anchors, g.BaselineY, layout);
                var col = ColumnOf(g.CenterX, layout);
                if (row < 0 || col < 0)
                {
                    if (!g.IsBlank)
                    {
                        orphans++;
                    }
                    continue;
                }
                (cells[row, col] ??= []).Add(g);
            }

            // Top of the page first, the order a reader sees.
            for (var r = anchors.Count - 1; r >= 0; r--)
            {
                var texts = new string?[HeaderLabels.Length];
                for (var c = 0; c < texts.Length; c++)
                {
                    texts[c] = cells[r, c] is { } list ? CellText(list) : null;
                }
                rows.Add(new KamRow(p + 1, texts));
            }
        }

        if (!headerChecked)
        {
            throw new KamFormatException("The PDF has no text. KAM's price lists are text-based; this one may be scanned or empty.");
        }

        return new KamTable(rows, snapshotAt, snapshotText, pages.Count, orphans, tableGlyphs);
    }

    /// <summary>
    /// "18.11.2026  5:46:25AM" (Skopje local, 12-hour clock with AM/PM, as live) → UTC.
    /// A 24-hour time without AM/PM is accepted too.
    /// </summary>
    internal static DateTimeOffset? ParseSnapshot(string? text)
    {
        var m = text is null ? Match.Empty : SnapshotRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }

        int I(string g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        var (day, month, year, hour, minute) = (I("d"), I("mo"), I("y"), I("h"), I("mi"));
        var second = m.Groups["s"].Success ? I("s") : 0;

        if (m.Groups["ampm"].Success)
        {
            if (hour is < 1 or > 12)
            {
                return null;
            }
            var pm = m.Groups["ampm"].Value.StartsWith('P') || m.Groups["ampm"].Value.StartsWith('p');
            hour = hour % 12 + (pm ? 12 : 0);
        }

        if (year is < 2000 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return SkopjeTime.ToUtc(new DateOnly(year, month, day), new TimeOnly(hour, minute, second));
    }

    internal static int ColumnOf(double x, KamTableLayout layout)
    {
        var edges = layout.ColumnEdges;
        for (var i = 0; i < edges.Count - 1; i++)
        {
            if (x >= edges[i] && x < edges[i + 1])
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Baselines of selling-price lines, ascending.</summary>
    private static List<double> FindAnchors(IReadOnlyList<KamGlyph> glyphs, KamTableLayout layout)
    {
        var priceColumn = glyphs.Where(g => ColumnOf(g.CenterX, layout) == (int)KamColumn.Price).ToList();
        return Lines(priceColumn)
            .Where(l => PriceLineRegex().IsMatch(LineText(l)))
            .Select(l => l[0].BaselineY)
            .OrderBy(y => y)
            .ToList();
    }

    /// <summary>Index of the owning anchor, or -1.</summary>
    private static int AnchorFor(List<double> ascendingAnchors, double y, KamTableLayout layout)
    {
        // The lowest anchor that is not more than RowRise below the glyph.
        for (var i = 0; i < ascendingAnchors.Count; i++)
        {
            var a = ascendingAnchors[i];
            if (a >= y - layout.RowRise)
            {
                return a - y <= layout.RowMaxDepth ? i : -1;
            }
        }
        return -1;
    }

    private static void CheckHeader(List<KamGlyph> header, KamTableLayout layout, int page)
    {
        var missing = new List<string>();
        for (var c = 0; c < HeaderLabels.Length; c++)
        {
            var inColumn = header.Where(g => ColumnOf(g.CenterX, layout) == c).ToList();
            var text = string.Join(' ', Lines(inColumn).Select(LineText)).ToLowerInvariant();
            if (!text.Contains(HeaderLabels[c], StringComparison.Ordinal))
            {
                missing.Add($"'{HeaderLabels[c]}' in column {c + 1}");
            }
        }

        if (missing.Count > 0)
        {
            throw new KamFormatException(
                $"Page {page}: the table header does not match the expected columns (missing {string.Join(", ", missing)}). " +
                "KAM may have changed the layout; re-measure Crawlers:Kam:ColumnEdges.");
        }
    }

    private static (DateTimeOffset? At, string? Text) FindSnapshot(List<KamGlyph> header)
    {
        foreach (var line in Lines(header))
        {
            var text = LineText(line);
            var m = SnapshotRegex().Match(text);
            if (m.Success)
            {
                return (ParseSnapshot(m.Value), MandatedFieldParser.CleanText(text));
            }
        }
        return (null, null);
    }

    private static string? CellText(List<KamGlyph> glyphs) =>
        MandatedFieldParser.CleanText(string.Join(' ', Lines(glyphs).Select(LineText)));

    /// <summary>Glyphs grouped into lines, top line first, each sorted left to right.</summary>
    private static List<List<KamGlyph>> Lines(IEnumerable<KamGlyph> glyphs)
    {
        var lines = new List<List<KamGlyph>>();
        foreach (var g in glyphs.OrderByDescending(g => g.BaselineY))
        {
            if (lines.Count > 0 && Math.Abs(lines[^1][0].BaselineY - g.BaselineY) <= SameBaseline)
            {
                lines[^1].Add(g);
            }
            else
            {
                lines.Add([g]);
            }
        }
        foreach (var line in lines)
        {
            line.Sort((a, b) => a.X.CompareTo(b.X));
        }
        return lines;
    }

    private static string LineText(List<KamGlyph> line)
    {
        var sb = new StringBuilder();
        KamGlyph? prev = null;
        foreach (var g in line)
        {
            if (prev is { } p && !p.IsBlank && !g.IsBlank && g.X - p.EndX > GapFactor * Math.Max(g.PointSize, 1))
            {
                sb.Append(' ');
            }
            sb.Append(g.Value);
            prev = g;
        }
        return sb.ToString();
    }

    // "102ден.", "1.299 ден."
    [GeneratedRegex(@"\d\s*ден", RegexOptions.IgnoreCase)]
    private static partial Regex PriceLineRegex();

    [GeneratedRegex(@"(?<d>\d{1,2})\.(?<mo>\d{1,2})\.(?<y>\d{4})\.?\s+(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2}))?\s*(?<ampm>[AaPp]\.?[Mm]\.?)?")]
    private static partial Regex SnapshotRegex();
}

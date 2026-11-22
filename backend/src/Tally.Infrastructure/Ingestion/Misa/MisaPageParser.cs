using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Misa;

/// <summary>One table row, cells already assigned to columns. Missing cells are null.</summary>
internal sealed record MisaRow(IReadOnlyDictionary<MisaColumn, string> Cells)
{
    public string? this[MisaColumn column] => Cells.TryGetValue(column, out var v) ? v : null;
}

/// <param name="Rows">Data rows on this page.</param>
/// <param name="LastPage">Highest <c>page=</c> number linked from this page, if any. With a
/// windowed paginator this is only the highest visible link, not necessarily the true last page.</param>
/// <param name="ContentHash">SHA-256 over the row text and last page; stable across
/// cosmetic markup changes (tokens, timestamps elsewhere on the page).</param>
/// <param name="TotalItems">Catalogue size the page reports ("од 5667 артикли"), if shown.</param>
internal sealed record MisaPage(IReadOnlyList<MisaRow> Rows, int? LastPage, string ContentHash, int? TotalItems = null);

/// <summary>
/// Pure HTML → rows. No I/O, so it is tested against saved pages.
/// </summary>
internal static partial class MisaPageParser
{
    public static MisaPage Parse(string html)
    {
        // New parser per call: cheap, and avoids relying on HtmlParser being thread-safe.
        using var doc = new HtmlParser().ParseDocument(html);

        var table = FindPriceTable(doc);
        var rows = table is null ? [] : ReadRows(table);
        var lastPage = FindLastPage(doc);

        // "Прикажи 1 - 100 од 5667 артикли": lets the adapter check it got everything.
        // The page's "Датум и време на последно ажурирање" is deliberately ignored: on
        // 2026-11-22 it showed the current minute on every load, not a publication time.
        var total = TotalRegex().Match(doc.Body?.TextContent ?? "") is { Success: true } m
                    && int.TryParse(m.Groups[1].Value, out var n) ? n : (int?)null;

        return new MisaPage(rows, lastPage, Hash(rows, lastPage), total);
    }

    /// <summary>
    /// The page is documented as having a single table; still pick the one whose header
    /// mentions the product name column, so an added layout table does not break parsing.
    /// </summary>
    private static IHtmlTableElement? FindPriceTable(IDocument doc)
    {
        var tables = doc.QuerySelectorAll("table").OfType<IHtmlTableElement>().ToList();
        return tables.FirstOrDefault(t => HeaderCells(t).Any(h => MisaColumnMap.Classify(h) == MisaColumn.Name))
               ?? (tables.Count == 1 ? tables[0] : null);
    }

    /// <summary>
    /// One label per data column, taken from the lowest header cell covering it. The live
    /// page has a two-row header: "Промотивна или попустна цена" spans three columns whose
    /// own labels ("Цена со попуст", "Вид на…", "Времетраење…") sit in the second row.
    /// </summary>
    private static List<string> HeaderCells(IHtmlTableElement table)
    {
        var headerRows = table.Head?.Rows.ToList()
                         ?? table.Rows.TakeWhile(r => r.Cells.Length > 0 && r.Cells.All(c => c.LocalName == "th")).ToList();
        if (headerRows.Count == 0)
        {
            return [];
        }

        var labels = new List<string>();
        var occupied = new HashSet<(int Row, int Col)>();
        for (var r = 0; r < headerRows.Count; r++)
        {
            var col = 0;
            foreach (var cell in headerRows[r].Cells)
            {
                while (occupied.Contains((r, col)))
                {
                    col++;
                }

                var text = MandatedFieldParser.CleanText(cell.TextContent) ?? "";
                var rowSpan = Math.Max(1, cell.RowSpan);
                var colSpan = Math.Max(1, cell.ColumnSpan);
                for (var dc = 0; dc < colSpan; dc++)
                {
                    for (var dr = 0; dr < rowSpan; dr++)
                    {
                        occupied.Add((r + dr, col + dc));
                    }
                    while (labels.Count <= col + dc)
                    {
                        labels.Add("");
                    }
                    labels[col + dc] = text; // a lower row overwrites: the leaf label wins
                }
                col += colSpan;
            }
        }
        return labels;
    }

    private static List<MisaRow> ReadRows(IHtmlTableElement table)
    {
        var map = MisaColumnMap.FromHeaders(HeaderCells(table));
        var rows = new List<MisaRow>();

        foreach (var tr in table.Rows)
        {
            // Skip header rows wherever they are.
            if (tr.ParentElement is IHtmlTableSectionElement { LocalName: "thead" }
                || tr.Cells.All(c => c.LocalName == "th"))
            {
                continue;
            }

            var cells = tr.Cells;

            // "No results" style rows: a single spanning cell.
            if (cells.Length <= 1)
            {
                continue;
            }

            var mapped = new Dictionary<MisaColumn, string>();
            for (var i = 0; i < cells.Length; i++)
            {
                var column = map[i];
                var text = MandatedFieldParser.CleanText(cells[i].TextContent);
                if (column is { } c && text is not null)
                {
                    mapped[c] = text;
                }
            }

            if (mapped.Count > 0)
            {
                rows.Add(new MisaRow(mapped));
            }
        }

        return rows;
    }

    private static int? FindLastPage(IDocument doc)
    {
        int? max = null;
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var m = PageParamRegex().Match(a.GetAttribute("href") ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                max = max is null ? n : Math.Max(max.Value, n);
            }
        }
        return max;
    }

    private static string Hash(IReadOnlyList<MisaRow> rows, int? lastPage)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            foreach (var (column, value) in row.Cells.OrderBy(kv => kv.Key))
            {
                sb.Append((int)column).Append('=').Append(value).Append('\u001f');
            }
            sb.Append('\u001e');
        }
        sb.Append("lp=").Append(lastPage);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    [GeneratedRegex(@"[?&]page=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PageParamRegex();

    [GeneratedRegex(@"од\s+(\d+)\s+артикл", RegexOptions.IgnoreCase)]
    private static partial Regex TotalRegex();
}

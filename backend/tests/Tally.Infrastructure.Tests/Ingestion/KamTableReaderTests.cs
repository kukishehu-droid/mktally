using Tally.Infrastructure.Ingestion.Kam;

namespace Tally.Infrastructure.Tests.Ingestion;

/// <summary>
/// The positional logic on hand-built glyphs. Coordinates copy the live layout (2026-11-22):
/// header labels at y 543.55, the stamp at 567.15, first row's price baseline at 431.9,
/// rows 37.45 pt apart, cells top-aligned.
/// </summary>
public class KamTableReaderTests
{
    private const double Row1 = 431.9;
    private const double Pitch = 37.45;

    [Fact]
    public void Wrapped_cells_attach_to_their_own_row_not_the_neighbour()
    {
        var page = Header()
            // Row 1: two-line name starting 1.95 above the price baseline, a three-line
            // description reaching 18.6 below it (the deepest seen live).
            .Concat(G("ЛЕБ ТОНУС", 17, Row1 + 1.95, 6.6))
            .Concat(G("450 Г", 27, Row1 - 7.8, 6.6))
            .Concat(G("75ден.", 112, Row1))
            .Concat(G("TONUS ЛЕБ", 266, Row1 + 0.95, 6.6))
            .Concat(G("СТАНДАРД", 270, Row1 - 8.8, 6.6))
            .Concat(G("ОД ЗРНО", 270, Row1 - 18.6, 6.6))
            .Concat(G("Да", 378, Row1))
            // Row 2, right below.
            .Concat(G("ЖЕЛЕ 200 ГР", 20, Row1 - Pitch + 1.95, 6.6))
            .Concat(G("46ден.", 112, Row1 - Pitch))
            .Concat(G("Не", 378, Row1 - Pitch))
            .ToList();

        var table = KamTableReader.Read([page], KamTableLayout.Default);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("ЛЕБ ТОНУС 450 Г", table.Rows[0][KamColumn.Name]);
        Assert.Equal("TONUS ЛЕБ СТАНДАРД ОД ЗРНО", table.Rows[0][KamColumn.Description]);
        Assert.Equal("Да", table.Rows[0][KamColumn.Availability]);
        Assert.Equal("ЖЕЛЕ 200 ГР", table.Rows[1][KamColumn.Name]);
        Assert.Equal("Не", table.Rows[1][KamColumn.Availability]);
        Assert.Equal(0, table.OrphanGlyphs);
    }

    [Fact]
    public void Header_and_stamp_are_not_rows_and_the_stamp_is_read()
    {
        var page = Header(stamp: "18.11.2026  5:46:25PM")
            .Concat(G("АРТИКЛ", 20, Row1))
            .Concat(G("10ден.", 112, Row1))
            .ToList();

        var table = KamTableReader.Read([page], KamTableLayout.Default);

        var row = Assert.Single(table.Rows);
        Assert.Equal("АРТИКЛ", row[KamColumn.Name]);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 16, 46, 25, TimeSpan.Zero), table.SnapshotAt);
    }

    [Fact]
    public void Empty_table_gives_no_rows_but_keeps_the_stamp()
    {
        // Store 69 on 2026-11-22: the header and nothing else, stamped 13.10.2026.
        var table = KamTableReader.Read([Header(stamp: "13.10.2026  5:48:25AM").ToList()], KamTableLayout.Default);

        Assert.Empty(table.Rows);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 3, 48, 25, TimeSpan.Zero), table.SnapshotAt);
    }

    [Fact]
    public void Glyphs_outside_every_row_and_column_are_counted_as_orphans()
    {
        var page = Header()
            .Concat(G("АРТИКЛ", 20, Row1))
            .Concat(G("10ден.", 112, Row1))
            .Concat(G("X", 805, Row1))      // right of the last column rule
            .Concat(G("Y", 20, Row1 - 90))  // far below the last row
            .ToList();

        var table = KamTableReader.Read([page], KamTableLayout.Default);

        Assert.Single(table.Rows);
        Assert.Equal(2, table.OrphanGlyphs);
    }

    [Fact]
    public void Header_repeated_on_later_pages_is_not_read_as_rows()
    {
        var p1 = Header().Concat(G("А", 20, Row1)).Concat(G("10ден.", 112, Row1)).ToList();
        var p2 = Header(stamp: null).Concat(G("Б", 20, Row1)).Concat(G("20ден.", 112, Row1)).ToList();

        var table = KamTableReader.Read([p1, p2], KamTableLayout.Default);

        Assert.Equal(new[] { "А", "Б" }, table.Rows.Select(r => r[KamColumn.Name]));
        Assert.Equal(new[] { 1, 2 }, table.Rows.Select(r => r.Page));
    }

    [Fact]
    public void Word_gaps_without_a_space_glyph_still_separate_words()
    {
        // Some PDF libraries drop drawn spaces; the gap alone must keep words apart.
        var page = Header()
            .Concat(G("ЛЕБ", 17, Row1, includeSpaces: false))
            .Concat(G("ТОНУС", 17 + 3 * 4.15 + 3, Row1, includeSpaces: false))
            .Concat(G("75ден.", 112, Row1))
            .ToList();

        Assert.Equal("ЛЕБ ТОНУС", KamTableReader.Read([page], KamTableLayout.Default).Rows[0][KamColumn.Name]);
    }

    [Fact]
    public void A_moved_column_fails_loudly()
    {
        var header = Header().Where(g => g.BaselineY != 543.55 || g.X < 600 || g.X >= 699).ToList(); // drop "Вид"
        var ex = Assert.Throws<KamFormatException>(() => KamTableReader.Read([header], KamTableLayout.Default));
        Assert.Contains("вид", ex.Message);
    }

    [Fact]
    public void A_pdf_without_text_fails()
    {
        Assert.Throws<KamFormatException>(() => KamTableReader.Read([[]], KamTableLayout.Default));
    }

    // ---- helpers ----

    /// <summary>Header labels where the live file draws them, plus the stamp line above.</summary>
    private static IEnumerable<KamGlyph> Header(string? stamp = "18.11.2026  5:46:25AM")
    {
        var labels = new (string Text, double X)[]
        {
            ("Назив", 11), ("Продажна", 88.75), ("Единична", 172), ("Опис", 259.75), ("Достапност", 341.5),
            ("Редовна", 435.75), ("попуст", 525.75), ("Вид", 604.75), ("Времетраење", 701.5),
        };
        var glyphs = labels.SelectMany(l => G(l.Text, l.X, 543.55, 11.6, advance: 0.5));
        return stamp is null
            ? glyphs
            : glyphs.Concat(G("Датум и време на последно ажурирање на цените: " + stamp, 5.5, 567.15, 8.3, advance: 0.45));
    }

    /// <summary>Text as glyphs with a fixed advance per character.</summary>
    private static IEnumerable<KamGlyph> G(string text, double x, double y, double size = 8.3,
        double advance = 0.5, bool includeSpaces = true)
    {
        var w = size * advance;
        foreach (var ch in text)
        {
            if (ch != ' ' || includeSpaces)
            {
                yield return new KamGlyph(ch.ToString(), x, x + w, y, size);
            }
            x += w;
        }
    }
}

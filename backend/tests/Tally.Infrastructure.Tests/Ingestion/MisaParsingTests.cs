using Tally.Infrastructure.Ingestion.Misa;

namespace Tally.Infrastructure.Tests.Ingestion;

public class MisaParsingTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Misa", name));

    [Fact]
    public void Parses_all_data_rows_and_ignores_header()
    {
        var page = MisaPageParser.Parse(Fixture("synthetic_page1.html"));

        Assert.Equal(5, page.Rows.Count);
        Assert.Equal("МАЛБОРО ГОЛД МЕКО", page.Rows[0][MisaColumn.Name]);
        Assert.Equal("210 ден.", page.Rows[0][MisaColumn.Price]);
    }

    [Fact]
    public void Ragged_rows_leave_trailing_columns_null()
    {
        var row = MisaPageParser.Parse(Fixture("synthetic_page1.html")).Rows[0];

        Assert.Null(row[MisaColumn.UnitPrice]);   // empty cell
        Assert.Null(row[MisaColumn.PromoPrice]);  // omitted cell
        Assert.Null(row[MisaColumn.PromoPeriod]);
    }

    [Fact]
    public void Reads_last_page_from_pagination_links()
    {
        Assert.Equal(3, MisaPageParser.Parse(Fixture("synthetic_page1.html")).LastPage);
    }

    [Fact]
    public void Content_hash_ignores_markup_outside_the_rows()
    {
        var html = Fixture("synthetic_page1.html");
        var a = MisaPageParser.Parse(html);
        var b = MisaPageParser.Parse(html.Replace("changes-every-request-abc123", "zzz999"));
        var c = MisaPageParser.Parse(html.Replace("210 ден.</td><td></td>", "215 ден.</td><td></td>"));

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }

    [Fact]
    public void Header_mapping_tolerates_reordered_columns()
    {
        const string html = """
            <table>
              <tr><th>Продажна цена</th><th>Назив на стока-производ</th><th>Достапност во продажен објект</th></tr>
              <tr><td>99 ден.</td><td>ЧОКОЛАДО</td><td>Не</td></tr>
            </table>
            """;
        var row = Assert.Single(MisaPageParser.Parse(html).Rows);

        Assert.Equal("ЧОКОЛАДО", row[MisaColumn.Name]);
        Assert.Equal("99 ден.", row[MisaColumn.Price]);
        Assert.Equal("Не", row[MisaColumn.Availability]);
    }

    [Fact]
    public void Unrecognised_headers_fail_loudly()
    {
        const string html = "<table><tr><th>Product</th><th>Cost</th></tr><tr><td>A</td><td>1</td></tr></table>";
        Assert.Throws<MisaFormatException>(() => MisaPageParser.Parse(html));
    }

    [Fact]
    public void Page_without_table_has_no_rows()
    {
        var page = MisaPageParser.Parse("<html><body><p>Нема резултати</p></body></html>");
        Assert.Empty(page.Rows);
        Assert.Null(page.LastPage);
    }

    [Fact]
    public void Maps_plain_row()
    {
        var row = MisaPageParser.Parse(Fixture("synthetic_page1.html")).Rows[0];
        Assert.True(MisaRecordMapper.TryMap(row, out var r, out _));

        Assert.Equal("МАЛБОРО ГОЛД МЕКО", r.NamePrimary);
        Assert.Equal(210, r.Price);
        Assert.Equal(210, r.RegularPrice);
        Assert.Null(r.PromoPrice);
        Assert.Null(r.DiscountPct);
        Assert.Null(r.UnitPrice);
        Assert.Null(r.UnitBasis);
        Assert.True(r.IsAvailable);
        Assert.Equal("ЦИГАРИ", r.Category);
        Assert.Null(r.PublishedOn);
        Assert.StartsWith("name:", r.SourceKey);
    }

    [Fact]
    public void Maps_unit_price_and_unavailability()
    {
        var row = MisaPageParser.Parse(Fixture("synthetic_page1.html")).Rows[2];
        Assert.True(MisaRecordMapper.TryMap(row, out var r, out _));

        Assert.Equal(16.66m, r.UnitPrice);
        Assert.Equal("100G", r.UnitBasis);
        Assert.False(r.IsAvailable);
    }

    [Fact]
    public void Maps_promo_row()
    {
        var row = MisaPageParser.Parse(Fixture("synthetic_page1.html")).Rows[3];
        Assert.True(MisaRecordMapper.TryMap(row, out var r, out _));

        Assert.Equal("ЈОГУРТ ЗДРАВЈЕ 1Л", r.NamePrimary); // whitespace collapsed
        Assert.Equal(59, r.Price);
        Assert.Equal(79, r.RegularPrice);
        Assert.Equal(59, r.PromoPrice);
        Assert.Equal(25m, r.DiscountPct);
        Assert.Equal("Акција", r.PromoType);
        Assert.NotNull(r.PromoFrom);
        Assert.NotNull(r.PromoTo);
    }

    [Fact]
    public void Price_below_regular_is_flagged_as_promo_even_without_promo_columns()
    {
        var row = new MisaRow(new Dictionary<MisaColumn, string>
        {
            [MisaColumn.Name] = "КАФЕ",
            [MisaColumn.Price] = "150 ден.",
            [MisaColumn.RegularPrice] = "200 ден.",
        });
        Assert.True(MisaRecordMapper.TryMap(row, out var r, out _));

        Assert.Equal(150, r.PromoPrice);
        Assert.Equal(25m, r.DiscountPct);
    }

    [Fact]
    public void Skips_row_without_selling_price()
    {
        var row = MisaPageParser.Parse(Fixture("synthetic_page1.html")).Rows[4];
        Assert.False(MisaRecordMapper.TryMap(row, out _, out var reason));
        Assert.Contains("selling price", reason);
    }

    // ---- live page (captured 2026-11-22, trimmed) ----

    [Fact]
    public void Live_page_rows_count_and_total()
    {
        var page = MisaPageParser.Parse(Fixture("org3_page1_trimmed.html"));

        Assert.Equal(10, page.Rows.Count);
        Assert.Equal(57, page.LastPage);
        Assert.Equal(5667, page.TotalItems);
    }

    [Fact]
    public void Live_two_row_header_maps_the_promo_sub_columns()
    {
        var row = MisaPageParser.Parse(Fixture("org3_page1_trimmed.html")).Rows[5];

        Assert.Equal("НИВЕА ГЕЛ ЗА БРИЧЕЊЕ 200МЛСЕНЗ.550302", row[MisaColumn.Name]);
        Assert.Equal("299 ден. Попуст:3%", row[MisaColumn.DiscountedPrice]);
        Assert.Equal("промотивна цена", row[MisaColumn.PromoType]);
        Assert.Equal("01.10.2026 до 31.12.2026", row[MisaColumn.PromoPeriod]);
    }

    [Fact]
    public void Live_promo_row_maps_price_percent_type_and_window()
    {
        var row = MisaPageParser.Parse(Fixture("org3_page1_trimmed.html")).Rows[5];
        Assert.True(MisaRecordMapper.TryMap(row, out var r, out _));

        Assert.Equal(299, r.Price);
        Assert.Equal(309, r.RegularPrice);
        Assert.Equal(299, r.PromoPrice);
        Assert.Equal(3m, r.DiscountPct);                 // published, not recomputed
        Assert.Equal(149.50m, r.UnitPrice);
        Assert.Equal("100ML", r.UnitBasis);
        Assert.Equal("промотивна цена", r.PromoType);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), r.PromoFrom);  // 01.10 00:00, UTC+2
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 22, 59, 59, TimeSpan.Zero), r.PromoTo); // 31.12 23:59:59, UTC+1
        Assert.Null(r.PublishedOn); // the page's stamp is the current time, never a publication date
    }

    [Fact]
    public void Live_plain_row_has_no_promo()
    {
        var row = MisaPageParser.Parse(Fixture("org3_page1_trimmed.html")).Rows[0];
        Assert.True(MisaRecordMapper.TryMap(row, out var r, out _));

        Assert.Equal("МАЛБОРО ГОЛД МЕКО", r.NamePrimary);
        Assert.Equal(210, r.Price);
        Assert.Null(r.PromoPrice);
        Assert.Null(r.PromoFrom);
    }

    [Fact]
    public void Live_latin_unit_spelling_in_unit_price()
    {
        var row = MisaPageParser.Parse(Fixture("org3_page1_trimmed.html")).Rows[9];
        Assert.True(MisaRecordMapper.TryMap(row, out var r, out _));

        Assert.Equal("ПОДРАВКА ГРАШОК 800 ГР", r.NamePrimary);
        Assert.Equal(12.38m, r.UnitPrice);
        Assert.Equal("100G", r.UnitBasis);
        Assert.Equal(18m, r.DiscountPct);
    }

    [Fact]
    public void Source_key_is_stable_across_case_and_whitespace()
    {
        Assert.Equal(
            MisaRecordMapper.SourceKeyFor("Јогурт  Здравје 1Л"),
            MisaRecordMapper.SourceKeyFor(" ЈОГУРТ ЗДРАВЈЕ 1Л"));
        Assert.NotEqual(
            MisaRecordMapper.SourceKeyFor("ЈОГУРТ ЗДРАВЈЕ 1Л"),
            MisaRecordMapper.SourceKeyFor("ЈОГУРТ ЗДРАВЈЕ 1.5Л"));
    }
}

using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Kam;

namespace Tally.Infrastructure.Tests.Ingestion;

public class KamParsingTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kam", name);

    // ---- store list (live capture) ----

    [Fact]
    public void Store_list_parses_the_live_shape()
    {
        var shops = KamShopList.Parse(File.ReadAllText(FixturePath("shop_list_sample.json")));

        Assert.Equal(new[] { 73, 38, 64, 69 }, shops.Select(s => s.Id));

        var genc = shops.Single(s => s.Id == 38);
        Assert.Equal("Генц", genc.Name); // live value has a trailing space
        Assert.Equal("Струга", genc.City);
        Assert.Equal(41.18688301123239, genc.Latitude);
        Assert.Equal(20.675163482205495, genc.Longitude);
        Assert.Equal("08:30-21:00", genc.WorkingHours);
        Assert.Equal("09:00-21:00", genc.SundayWorkingHours);
        Assert.Equal("071/291-388", genc.Phone);
        Assert.Equal(new[] { "bakery", "parking" }, genc.Tags);

        var file = Assert.IsType<KamShopFile>(genc.CurrentFile);
        Assert.Equal("2026/11/22/38.pdf", file.RelativePath);
        // Midnight 22 November, Skopje time.
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 23, 0, 0, TimeSpan.Zero), file.UploadedAt);
    }

    [Theory]
    [InlineData("/Date(1795302000000)/", 1795302000000L)]
    [InlineData("/Date(1795302000000+0100)/", 1795302000000L)]
    public void Asp_net_dates_parse(string text, long expectedMs) =>
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(expectedMs), KamShopList.ParseAspNetDate(text));

    [Theory]
    [InlineData("41.18688301123239, 20.675163482205495", true)]
    [InlineData("41.18,20.67", true)]
    [InlineData("0, 0", false)]       // outside North Macedonia
    [InlineData("41.18", false)]
    [InlineData(null, false)]
    public void Coordinates_parse_and_are_bounded(string? text, bool ok)
    {
        var (lat, lon) = KamShopList.ParseCoordinates(text);
        Assert.Equal(ok, lat is not null && lon is not null);
    }

    [Fact]
    public void Store_list_that_is_not_an_array_fails_clearly()
    {
        Assert.Throws<KamFormatException>(() => KamShopList.Parse("<html>maintenance</html>"));
        Assert.Throws<KamFormatException>(() => KamShopList.Parse("""{"error":"x"}"""));
    }

    [Fact]
    public void A_store_without_an_id_is_skipped_not_fatal()
    {
        var shops = KamShopList.Parse("""[{"Name":"x"},{"Id":5,"Name":"Y","ShopFiles":[]}]""");
        var shop = Assert.Single(shops);
        Assert.Null(shop.CurrentFile);
    }

    // ---- snapshot stamp ----

    [Theory]
    [InlineData("18.11.2026  5:46:25AM", "2026-11-18T04:46:25Z")] // live; CET, UTC+1
    [InlineData("13.10.2026 5:48:25PM", "2026-10-13T15:48:25Z")]  // store 69's stamp, if PM; CEST, UTC+2
    [InlineData("13.10.2026 12:05:00AM", "2026-10-12T22:05:00Z")] // 12 AM is midnight
    [InlineData("13.10.2026 12:05:00PM", "2026-10-13T10:05:00Z")] // 12 PM is noon
    [InlineData("18.11.2026 17:46", "2026-11-18T16:46:00Z")]      // 24-hour form
    public void Snapshot_stamp_parses_as_Skopje_time(string text, string expectedUtc) =>
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture),
            KamTableReader.ParseSnapshot(text));

    [Theory]
    [InlineData("31.02.2026 5:00:00AM")]
    [InlineData("18.11.2026 13:00:00PM")]
    [InlineData("no stamp")]
    public void Bad_snapshot_stamps_are_null(string text) => Assert.Null(KamTableReader.ParseSnapshot(text));

    // ---- the real PDF, end to end through PdfPig ----

    private static readonly Lazy<KamTable> LiveTable = new(() =>
        KamTableReader.Read(KamPdfReader.Read(File.ReadAllBytes(FixturePath("price_38_trimmed.pdf"))), KamTableLayout.Default));

    private static IReadOnlyList<RawPriceRecord> LiveRecords()
    {
        var list = new List<RawPriceRecord>();
        foreach (var row in LiveTable.Value.Rows)
        {
            Assert.True(KamRecordMapper.TryMap(row, new DateOnly(2026, 11, 18), out var r, out var reason), reason);
            list.Add(r);
        }
        return list;
    }

    [Fact]
    public void Live_pdf_reads_every_row_and_nothing_else()
    {
        var table = LiveTable.Value;

        Assert.Equal(3, table.Pages);
        Assert.Equal(33, table.Rows.Count); // 11 per page
        Assert.Equal(0, table.OrphanGlyphs);
        Assert.Equal(new[] { 11, 11, 11 }, table.Rows.GroupBy(r => r.Page).Select(g => g.Count()));
    }

    [Fact]
    public void Live_pdf_snapshot_is_the_stamp_not_the_url_date()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 4, 46, 25, TimeSpan.Zero), LiveTable.Value.SnapshotAt);
        Assert.Contains("18.11.2026", LiveTable.Value.SnapshotText);
    }

    [Fact]
    public void Live_pdf_first_row_joins_wrapped_lines_with_spaces()
    {
        var row = LiveTable.Value.Rows[0];

        Assert.Equal("БОНБОНИ ЕВРОПА НОИР 100ГР", row[KamColumn.Name]);
        Assert.Equal("24ден.", row[KamColumn.Price]);
        Assert.Null(row[KamColumn.UnitPrice]);
        Assert.Equal("EVROPA БОНБОНИ NOIRE", row[KamColumn.Description]);
        Assert.Equal("Да", row[KamColumn.Availability]);
        Assert.Equal("24ден.", row[KamColumn.RegularPrice]);
        Assert.Null(row[KamColumn.DiscountedPrice]);
        Assert.Null(row[KamColumn.PromoType]);
        Assert.Null(row[KamColumn.PromoPeriod]);
    }

    [Fact]
    public void Live_pdf_plain_row_maps()
    {
        var r = LiveRecords().Single(x => x.NamePrimary == "ЛЕБ ТОНУС 450 Г СТАНДАРД");

        Assert.Equal(75, r.Price);
        Assert.Equal(75, r.RegularPrice);
        Assert.Null(r.PromoPrice);
        Assert.Equal(16.66m, r.UnitPrice);
        Assert.Equal("100G", r.UnitBasis);
        Assert.Equal("TONUS ЛЕБ СТАНДАРД ОД ИЗРТЕНО ЗРНО", r.NameAlt);
        Assert.True(r.IsAvailable);
        Assert.Null(r.Category);
        Assert.Equal(new DateOnly(2026, 11, 18), r.PublishedOn);
    }

    [Fact]
    public void Live_pdf_promo_with_regular_price_maps()
    {
        var r = LiveRecords().Single(x => x.NamePrimary == "ДУО КЕКС ГРИСОН 500ГР");

        Assert.Equal(102, r.Price);
        Assert.Equal(115, r.RegularPrice);
        Assert.Equal(102, r.PromoPrice);
        Assert.Equal(11m, r.DiscountPct);
        Assert.Equal("промотивна цена", r.PromoType);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 23, 0, 0, TimeSpan.Zero), r.PromoFrom);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 22, 59, 59, TimeSpan.Zero), r.PromoTo);
        // Published as "1 гр = 204.00 ден.": 102 den for 500 g is 204 den per kilogram.
        Assert.Equal(204m, r.UnitPrice);
        Assert.Equal("1KG", r.UnitBasis);
        Assert.Equal("GRIESSON БИСКВИТ СЕНДВИЧ 38% КАКАО КРЕМ", r.NameAlt);
    }

    [Fact]
    public void Live_pdf_promo_without_regular_price_is_still_flagged()
    {
        var r = LiveRecords().Single(x => x.NamePrimary == "МИСИРКА ЦЕЛА ЗАМРЗНАТА РОНСАРД 2.8 КГ");

        Assert.Equal(999, r.Price);
        Assert.Null(r.RegularPrice);
        Assert.Equal(999, r.PromoPrice);
        Assert.Null(r.DiscountPct);
        Assert.Equal("промотивна цена", r.PromoType);
        Assert.NotNull(r.PromoTo);
        Assert.Equal(356.78m, r.UnitPrice);
        Assert.Equal("1KG", r.UnitBasis);
    }

    [Fact]
    public void Live_pdf_source_keys_are_unique()
    {
        var records = LiveRecords();
        Assert.Equal(records.Count, records.Select(r => r.SourceKey).Distinct().Count());
        Assert.All(records, r => Assert.StartsWith("name:", r.SourceKey));
    }

    // ---- unit-basis correction ----

    [Theory]
    [InlineData("1 гр = 204.00 ден.", 204.00, "1KG")]   // KAM's "1 гр" is per kilogram
    [InlineData("1 мл = 99.80 ден.", 99.80, "1L")]      // and "1 мл" per litre
    [InlineData("1 кг = 356.78 ден.", 356.78, "1KG")]
    [InlineData("100 гр = 31.78 ден.", 31.78, "100G")]
    [InlineData("100 МЛ = 8.25 ден.", 8.25, "100ML")]
    [InlineData("1 Л = 158.50 ден.", 158.50, "1L")]
    [InlineData("1 пар = 3.50 ден.", 3.50, "1PC")]
    public void Unit_basis_is_corrected_where_KAM_is_systematically_wrong(string cell, double price, string basis)
    {
        var unit = KamRecordMapper.CorrectUnit(Tally.Infrastructure.Ingestion.Parsing.MandatedFieldParser.ParseUnitPrice(cell));
        Assert.Equal<(decimal, string)?>(((decimal)price, basis), unit);
    }

    [Theory]
    [InlineData("100 кг = 27.50 ден.")]  // a 200 g drink: impossible basis
    [InlineData("100 пар = 28.80 ден.")] // a 250 g pastry: wrong unit
    [InlineData("1 = 33.75 ден.")]       // no unit
    [InlineData(null)]
    public void Untrustworthy_unit_prices_are_dropped(string? cell) =>
        Assert.Null(KamRecordMapper.CorrectUnit(Tally.Infrastructure.Ingestion.Parsing.MandatedFieldParser.ParseUnitPrice(cell)));

    // ---- mapper edge cases ----

    private static KamRow Row(string? name = "АРТИКЛ", string? price = "100ден.", string? regular = "100ден.",
        string? discounted = null, string? type = null, string? period = null, string? availability = "Да") =>
        new(1, [name, price, null, "BRAND ОПИС", availability, regular, discounted, type, period]);

    [Fact]
    public void Selling_below_regular_is_a_promo_even_without_a_promo_cell()
    {
        Assert.True(KamRecordMapper.TryMap(Row(price: "80ден."), new DateOnly(2026, 11, 18), out var r, out _));
        Assert.Equal(80, r.PromoPrice);
        Assert.Equal(20m, r.DiscountPct);
    }

    [Fact]
    public void Promo_cell_equal_to_regular_is_not_a_promo()
    {
        Assert.True(KamRecordMapper.TryMap(Row(discounted: "100ден.", type: "промотивна цена"), new DateOnly(2026, 11, 18), out var r, out _));
        Assert.Null(r.PromoPrice);
        Assert.Null(r.PromoType);
    }

    [Theory]
    [InlineData(null, "100ден.")]
    [InlineData("АРТИКЛ", null)]
    [InlineData("АРТИКЛ", "ден.")]
    public void Rows_without_name_or_price_are_skipped(string? name, string? price)
    {
        Assert.False(KamRecordMapper.TryMap(Row(name, price), new DateOnly(2026, 11, 18), out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Unavailable_is_read()
    {
        Assert.True(KamRecordMapper.TryMap(Row(availability: "Не"), new DateOnly(2026, 11, 18), out var r, out _));
        Assert.False(r.IsAvailable);
    }
}

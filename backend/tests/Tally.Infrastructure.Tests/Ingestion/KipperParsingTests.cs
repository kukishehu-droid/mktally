using System.Text.Json;
using Tally.Infrastructure.Ingestion.Kipper;

namespace Tally.Infrastructure.Tests.Ingestion;

public class KipperParsingTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kipper", name));

    private static readonly DateOnly Published = new(2026, 11, 20);

    // ---- store page: live capture ----

    [Fact]
    public void Live_store_page_yields_timestamp_coordinates_and_post_id()
    {
        var page = KipperStorePageParser.Parse(Fixture("store_131_trimmed.html"));

        // "20 November 2026 - 15:43" Skopje = 14:43 UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 14, 43, 0, TimeSpan.Zero), page.LastPriceUpdate);
        Assert.Equal(41.1754493, page.Latitude);
        Assert.Equal(20.6745462, page.Longitude);
        Assert.Equal("22911", page.PostId);
    }

    [Fact]
    public void Variant_store_page_directions_link_macedonian_month_and_shortlink_fallback()
    {
        var page = KipperStorePageParser.Parse(Fixture("synthetic_store_page.html"));

        Assert.Equal(new DateTimeOffset(2026, 11, 22, 5, 15, 0, TimeSpan.Zero), page.LastPriceUpdate);
        Assert.Equal(41.1908408, page.Latitude);   // the destination, not the "@" viewport centre
        Assert.Equal(20.6767341, page.Longitude);
        Assert.Equal("22732", page.PostId);
    }

    [Theory]
    [InlineData("20 November 2026 - 15:43", 2026, 11, 20, 14, 43)]
    [InlineData("5 nëntor 2026 08:00", 2026, 11, 5, 7, 0)]
    [InlineData("1 јули 2026 - 10:00", 2026, 7, 1, 8, 0)]   // summer time, UTC+2
    [InlineData("3 Dec 2026", 2026, 12, 2, 23, 0)]          // date only: local midnight
    public void Named_month_timestamps(string text, int y, int mo, int d, int h, int mi) =>
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), KipperStorePageParser.ParseNamedMonth(text));

    [Theory]
    [InlineData("https://www.google.com/maps?q=41.1754493,20.6745462")]
    [InlineData("https://maps.google.com/maps?ll=41.1754493,20.6745462&z=17")]
    [InlineData("https://www.google.com/maps/embed?pb=!1m18!3d41.1754493!4d20.6745462")]
    [InlineData("https://www.google.com/maps/dir/?api=1&destination=41.1754493%2C20.6745462")]
    [InlineData("https://www.google.com/maps/@41.1754493,20.6745462,17.75z?entry=ttu")]
    public void Coordinates_from_common_google_maps_link_forms(string url)
    {
        var (lat, lon) = KipperStorePageParser.FindCoordinates([url]);
        Assert.Equal(41.1754493, lat);
        Assert.Equal(20.6745462, lon);
    }

    [Fact]
    public void Coordinates_outside_north_macedonia_are_rejected()
    {
        var (lat, lon) = KipperStorePageParser.FindCoordinates(["https://www.google.com/maps/@48.8566,2.3522,12z"]);
        Assert.Null(lat);
        Assert.Null(lon);
    }

    [Fact]
    public void Missing_timestamp_and_post_id_are_null_not_errors()
    {
        var page = KipperStorePageParser.Parse("<html><body><h1>Кипер</h1></body></html>");
        Assert.Null(page.LastPriceUpdate);
        Assert.Null(page.PostId);
    }

    // ---- ajax envelope ----

    [Fact]
    public void Live_envelope_has_counts_as_strings()
    {
        var page = KipperDataPage.Parse(Fixture("ajax_131_sample.json"));
        Assert.Equal(1, page.Draw);
        Assert.Equal(2190, page.RecordsTotal);
        Assert.Equal(2190, page.RecordsFiltered);
        Assert.Equal(4, page.Rows.Count);
    }

    [Theory]
    [InlineData("0", "not registered")]
    [InlineData("-1", "nonce")]
    [InlineData("<html>oops</html>", "not JSON")]
    [InlineData("{\"error\":\"x\"}", "no 'data'")]
    public void Envelope_explains_wordpress_failures(string body, string expected)
    {
        var ex = Assert.Throws<KipperFormatException>(() => KipperDataPage.Parse(body));
        Assert.Contains(expected, ex.Message);
    }

    // ---- records: live rows ----

    private static JsonElement LiveRow(int index) =>
        KipperDataPage.Parse(Fixture("ajax_131_sample.json")).Rows[index];

    [Fact]
    public void Maps_the_live_sample_record()
    {
        Assert.True(KipperRecordMapper.TryMap(LiveRow(0), Published, out var r, out _));

        Assert.Equal("code:000307", r.SourceKey);
        Assert.Equal("7ДЕЈС МИНИ КРОАСАН КАКАО 60г", r.NamePrimary);
        Assert.Equal("7DAYS MINI KROASAN KAKAO 60gr", r.NameAlt);
        Assert.Equal("КИФЛИ И ТОРТИЧКИ", r.Category);
        Assert.Equal(36, r.Price);
        Assert.Equal(36, r.RegularPrice);
        Assert.Null(r.PromoPrice);
        Assert.Equal(600m, r.UnitPrice);
        Assert.Equal("1KG", r.UnitBasis);
        Assert.Null(r.IsAvailable);
        Assert.Equal(Published, r.PublishedOn);
    }

    [Fact]
    public void Live_pair_unit_reads_as_pieces()
    {
        Assert.True(KipperRecordMapper.TryMap(LiveRow(2), Published, out var r, out _));
        Assert.Equal(12m, r.UnitPrice);
        Assert.Equal("1PC", r.UnitBasis);
    }

    [Fact]
    public void Announced_promotion_at_normal_price_is_not_a_promo_yet()
    {
        // Live row: price 89 = normal 89, promotion window 24-25 Nov announced in advance.
        Assert.True(KipperRecordMapper.TryMap(LiveRow(3), Published, out var r, out _));
        Assert.Equal(89, r.Price);
        Assert.Null(r.PromoPrice);
        Assert.Null(r.PromoFrom);
        Assert.Null(r.PromoTo);
    }

    // ---- records: edge cases (synthetic) ----

    private static JsonElement SyntheticRow(int index) =>
        KipperDataPage.Parse(Fixture("synthetic_ajax_page.json")).Rows[index];

    [Fact]
    public void Maps_promo_with_window_and_numeric_price()
    {
        Assert.True(KipperRecordMapper.TryMap(SyntheticRow(1), Published, out var r, out _));

        Assert.Equal(59, r.Price);
        Assert.Equal(72, r.RegularPrice);
        Assert.Equal(59, r.PromoPrice);
        Assert.Equal(18m, r.DiscountPct);
        Assert.Equal("1L", r.UnitBasis);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 23, 0, 0, TimeSpan.Zero), r.PromoFrom);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 22, 59, 0, TimeSpan.Zero), r.PromoTo);
    }

    [Fact]
    public void Missing_image_falls_back_to_name_key()
    {
        Assert.True(KipperRecordMapper.TryMap(SyntheticRow(2), Published, out var r, out _));
        Assert.StartsWith("name:", r.SourceKey);
        Assert.Equal("1PC", r.UnitBasis);
    }

    [Fact]
    public void Skips_record_without_price()
    {
        Assert.False(KipperRecordMapper.TryMap(SyntheticRow(3), Published, out _, out var reason));
        Assert.Contains("product_price", reason);
    }

    [Fact]
    public void Discount_is_computed_when_not_published()
    {
        using var doc = JsonDocument.Parse(
            "{\"product_name\":\"КАФЕ\",\"product_price\":\"150\",\"product_price_normal\":\"200\"}");
        Assert.True(KipperRecordMapper.TryMap(doc.RootElement, null, out var r, out _));
        Assert.Equal(150, r.PromoPrice);
        Assert.Equal(25m, r.DiscountPct);
    }

    [Theory]
    [InlineData("1КГ=", "600", "600", "1KG")]
    [InlineData("=1КГ", "600", "600", "1KG")]   // reversed form, seen live
    [InlineData("=1Л", "59", "59", "1L")]       // reversed form, seen live
    [InlineData("1кг=", "52", "52", "1KG")]
    [InlineData("100гр=", "12", "12", "100G")]
    [InlineData("1ПАР=", "12", "12", "1PC")]
    public void Unit_label_variants(string type, string value, string price, string basis)
    {
        var unit = KipperRecordMapper.ParseUnit(type, value);
        Assert.Equal(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), unit?.Price);
        Assert.Equal(basis, unit?.Basis);
    }

    [Fact]
    public void Empty_unit_label_gives_no_unit_price() =>
        Assert.Null(KipperRecordMapper.ParseUnit("", "600"));

    [Theory]
    [InlineData("000307.webp", "code:000307")]
    [InlineData("noimage.webp", null)]
    [InlineData("12.jpg", null)]
    public void Source_key_uses_numeric_image_stem_only(string image, string? expected)
    {
        var key = KipperRecordMapper.SourceKeyFor(image, "X");
        if (expected is null)
        {
            Assert.StartsWith("name:", key);
        }
        else
        {
            Assert.Equal(expected, key);
        }
    }
}

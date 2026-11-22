using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Tests.Ingestion;

public class MandatedFieldParserTests
{
    [Theory]
    [InlineData("210 ден.", 210)]
    [InlineData("89ден.", 89)]
    [InlineData("1.250 ден.", 1250)]
    [InlineData("1 250 ден.", 1250)]
    [InlineData("1 250 ден.", 1250)]
    [InlineData("89,50 ден.", 90)]
    [InlineData("89.49", 89)]
    public void ParseDenars_reads_published_formats(string text, int expected) =>
        Assert.Equal(expected, MandatedFieldParser.ParseDenars(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ден.")]
    public void ParseDenars_returns_null_for_empty(string? text) =>
        Assert.Null(MandatedFieldParser.ParseDenars(text));

    [Theory]
    [InlineData("100 гр = 31.78 ден.", "31.78", "100G")]
    [InlineData("100 гр = 16.66 ден.", "16.66", "100G")]
    [InlineData("1 кг = 600 ден.", "600", "1KG")]
    [InlineData("1КГ=600", "600", "1KG")]
    [InlineData("КГ=600", "600", "1KG")]
    [InlineData("1 л = 59,00 ден.", "59", "1L")]
    [InlineData("100 мл = 12.50 ден.", "12.50", "100ML")]
    [InlineData("1 ком = 15 ден.", "15", "1PC")]
    public void ParseUnitPrice_reads_basis_and_price(string text, string price, string basis)
    {
        var result = MandatedFieldParser.ParseUnitPrice(text);
        Assert.NotNull(result);
        Assert.Equal(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), result.Value.Price);
        Assert.Equal(basis, result.Value.Basis);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1КГ=")]
    [InlineData("100 бочки = 5 ден.")]
    public void ParseUnitPrice_rejects_missing_or_unknown(string text) =>
        Assert.Null(MandatedFieldParser.ParseUnitPrice(text));

    [Theory]
    [InlineData("Да", true)]
    [InlineData(" да ", true)]
    [InlineData("Не", false)]
    [InlineData("", null)]
    [InlineData("?", null)]
    public void ParseAvailability(string text, bool? expected) =>
        Assert.Equal(expected, MandatedFieldParser.ParseAvailability(text));

    [Fact]
    public void ParsePromoWindow_range_is_skopje_local_whole_days()
    {
        var (from, to) = MandatedFieldParser.ParsePromoWindow("01.11.2026 - 30.11.2026");

        // November: Skopje is UTC+1.
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 22, 59, 59, TimeSpan.Zero), to);
    }

    [Fact]
    public void ParsePromoWindow_respects_summer_time_and_explicit_times()
    {
        var (from, to) = MandatedFieldParser.ParsePromoWindow("од 01.07.2026 08:00 до 15.07.2026 21:00");

        // July: Skopje is UTC+2.
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 19, 0, 0, TimeSpan.Zero), to);
    }

    [Fact]
    public void ParsePromoWindow_single_date_after_do_is_end()
    {
        var (from, to) = MandatedFieldParser.ParsePromoWindow("до 30.11.2026");
        Assert.Null(from);
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 22, 59, 59, TimeSpan.Zero), to);
    }

    [Theory]
    [InlineData("299 ден. Попуст:3%", 299, "3")]   // live Misa cell
    [InlineData("59 ден.", 59, null)]
    [InlineData("-25%", null, "25")]
    [InlineData("1 250 ден.", 1250, null)]
    [InlineData("85", 85, null)]
    public void ParsePriceAndPercent(string text, int? denars, string? percent)
    {
        var (d, p) = MandatedFieldParser.ParsePriceAndPercent(text);
        Assert.Equal(denars, d);
        Assert.Equal(percent is null ? (decimal?)null : decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture), p);
    }

    [Fact]
    public void ParsePromoWindow_empty()
    {
        var (from, to) = MandatedFieldParser.ParsePromoWindow("");
        Assert.Null(from);
        Assert.Null(to);
    }
}

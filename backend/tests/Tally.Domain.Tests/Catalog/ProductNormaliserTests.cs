using Tally.Domain.Catalog;

namespace Tally.Domain.Tests.Catalog;

public class ProductNormaliserTests
{
    // Mirrors db/seed/reference/040_brand.sql.
    private static readonly ProductNormaliser Normaliser = new(new BrandLexicon(
    [
        new BrandEntry("ZDRAVJE", ["ЗДРАВЈЕ"]),
        new BrandEntry("TONUS", ["ТОНУС"]),
        new BrandEntry("7DAYS", ["7 DAYS", "7ДЕЈС"]),
        new BrandEntry("MILORD", ["МИЛОРД"]),
        new BrandEntry("DORA", ["ДОРА"]),
        new BrandEntry("AGRINA", ["АГРИНА"]),
        new BrandEntry("MARLBORO", ["МАЛБОРО", "MALBORO"]),
    ]));

    private static NormalisedProduct N(string name, string? alt = null, int? price = null,
        decimal? unitPrice = null, string? basis = null) =>
        Normaliser.Normalise(new ProductNameInput(name, alt, price, unitPrice, basis));

    // ---- exact key ----

    [Fact]
    public void KAM_sample_gets_an_exact_key()
    {
        var r = N("ЛЕБ ТОНУС 450 Г СТАНДАРД");

        Assert.Equal("TONUS", r.Brand);
        Assert.Equal(BrandSource.Lexicon, r.BrandSource);
        Assert.Equal("LEB STANDARD", r.Descriptor);
        Assert.Equal("450G", r.Size?.Key);
        Assert.Equal(SizeSource.Name, r.SizeSource);
        Assert.Equal("TONUS|LEB STANDARD|450G", r.ExactKey);
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void Word_order_does_not_change_the_key() =>
        Assert.Equal(N("ЛЕБ ТОНУС 450 Г СТАНДАРД").ExactKey, N("ТОНУС ЛЕБ СТАНДАРД 450Г").ExactKey);

    [Fact]
    public void Kipper_sample_with_consistent_unit_price()
    {
        var r = N("7ДЕЈС МИНИ КРОАСАН КАКАО 60г", "7DAYS MINI KROASAN KAKAO 60gr", 36, 600m, "1KG");

        Assert.Equal("7DAYS", r.Brand);
        Assert.Equal("MINI KROASAN KAKAO", r.Descriptor);
        Assert.Equal("7DAYS|KAKAO KROASAN MINI|60G", r.ExactKey);
        Assert.Empty(r.Flags);
    }

    [Fact]
    public void Multi_word_alias()
    {
        var r = N("7 DAYS KROASAN 60G");
        Assert.Equal("7DAYS", r.Brand);
        Assert.Equal("7DAYS|KROASAN|60G", r.ExactKey);
    }

    [Fact]
    public void Brand_from_the_albanian_name_when_the_macedonian_lacks_it()
    {
        var r = N("МИНИ КРОАСАН 60Г", "7DAYS MINI KROASAN 60gr");
        Assert.Equal("7DAYS", r.Brand);
        Assert.Equal("MINI KROASAN", r.Descriptor);
        Assert.Equal("7DAYS|KROASAN MINI|60G", r.ExactKey);
    }

    [Fact]
    public void Size_from_the_albanian_name_when_the_macedonian_lacks_it()
    {
        var r = N("ЗДРАВЈЕ МЛЕКО", "ZDRAVJE QUMESHT 1L");
        Assert.Equal("1000ML", r.Size?.Key);
        Assert.Equal(SizeSource.AltName, r.SizeSource);
        Assert.Equal("ZDRAVJE|MLEKO|1000ML", r.ExactKey);
    }

    // ---- the unit price as a check on size (code veto) ----

    [Fact]
    public void Unit_price_contradicting_the_size_blocks_the_key()
    {
        // 59 den at 118 den/L implies 500 ml, not 1 L.
        var r = N("МЛЕКО ЗДРАВЈЕ 1Л", price: 59, unitPrice: 118m, basis: "1L");

        Assert.Equal("1000ML", r.Size?.Key);
        Assert.Contains(NormalisationFlags.SizeUnitPriceMismatch, r.Flags);
        Assert.Null(r.ExactKey);
    }

    [Fact]
    public void Unit_price_confirms_a_unitless_number_and_it_leaves_the_descriptor()
    {
        // KAM sample: 89 den at 31.78 den/100 g = 280.05 g, and the name says "ФИЛ280".
        var r = N("ТОРТ. ДОРА КАКАО ФИЛ280", price: 89, unitPrice: 31.78m, basis: "100G");

        Assert.Equal("280G", r.Size?.Key);
        Assert.Equal(SizeSource.UnitPriceConfirmed, r.SizeSource);
        Assert.Equal("TORT KAKAO FIL", r.Descriptor);
        Assert.Equal("DORA|FIL KAKAO TORT|280G", r.ExactKey);
    }

    [Fact]
    public void Size_inferred_from_unit_price_alone_is_not_trusted()
    {
        var r = N("ДОМАТИ", price: 60, unitPrice: 60m, basis: "1KG");

        Assert.Equal("1000G", r.Size?.Key);
        Assert.Equal(SizeSource.UnitPriceInferred, r.SizeSource);
        Assert.Contains(NormalisationFlags.SizeInferred, r.Flags);
        Assert.Null(r.ExactKey);
    }

    [Fact]
    public void Consistent_unit_price_raises_no_flag()
    {
        // KAM sample: 46 den at 23.00 den/100 g = 200 g.
        var r = N("ЖЕЛЕ 200 ГР", price: 46, unitPrice: 23.00m, basis: "100G");
        Assert.Empty(r.Flags);
    }

    // ---- reasons for no key ----

    [Fact]
    public void Guessed_brand_is_a_hint_not_a_key()
    {
        var r = N("ВИТАМИНКА СМОКИ 50Г");

        Assert.Equal("VITAMINKA", r.Brand);
        Assert.Equal(BrandSource.LeadingToken, r.BrandSource);
        Assert.Equal("VITAMINKA SMOKI", r.Descriptor); // kept: a guess may be a product word
        Assert.Null(r.ExactKey);
    }

    [Theory]
    [InlineData("МЛЕКО 1Л")]
    [InlineData("ЖЕЛЕ 200 ГР")]
    public void Generic_leading_word_is_not_a_brand(string name)
    {
        var r = N(name);
        Assert.Null(r.Brand);
        Assert.Equal(BrandSource.None, r.BrandSource);
        Assert.Null(r.ExactKey);
    }

    [Fact]
    public void No_size_no_key()
    {
        var r = N("МАЛБОРО ГОЛД МЕКО");
        Assert.Equal("MARLBORO", r.Brand);
        Assert.Null(r.Size);
        Assert.Null(r.ExactKey);
    }

    [Fact]
    public void Bundle_blocks_the_key()
    {
        var r = N("ТОНУС ЛЕБ 450Г 2+1");
        Assert.Contains(NormalisationFlags.Bundle, r.Flags);
        Assert.Null(r.ExactKey);
    }

    [Fact]
    public void Macedonian_and_albanian_sizes_disagreeing_blocks_the_key()
    {
        var r = N("ЗДРАВЈЕ МЛЕКО 1Л", "ZDRAVJE QUMESHT 0.5L");
        Assert.Equal("1000ML", r.Size?.Key);
        Assert.Contains(NormalisationFlags.SizeConflictsWithAlt, r.Flags);
        Assert.Null(r.ExactKey);
    }

    [Fact]
    public void Ambiguous_size_blocks_the_key()
    {
        var r = N("ЗДРАВЈЕ СОК 1Л 500МЛ");
        Assert.Null(r.Size);
        Assert.Contains(NormalisationFlags.SizeAmbiguous, r.Flags);
        Assert.Null(r.ExactKey);
    }

    // ---- real names, captured 2026-11-22 ----

    private static readonly ProductNormaliser WithLiveBrands = new(new BrandLexicon(
    [
        new BrandEntry("NIVEA", ["НИВЕА"]),
        new BrandEntry("ADA", ["АДА"]),
    ]));

    [Fact]
    public void Misa_supplier_code_is_dropped_from_the_descriptor()
    {
        var r = WithLiveBrands.Normalise(new ProductNameInput("НИВЕА КРЕМ 75 МЛ.550002", Price: 129, UnitPrice: 172.00m, UnitBasis: "100ML"));

        Assert.Equal("KREM", r.Descriptor);
        Assert.Equal("NIVEA|KREM|75ML", r.ExactKey);
    }

    [Fact]
    public void Misa_glued_size_and_code_are_recovered_through_the_unit_price()
    {
        // "200МЛСЕНЗ": size glued to an abbreviation, so the name alone gives no size.
        // 299 den at 149.50 den/100 ml confirms 200 ml, and the glued "ML" goes with it.
        var r = WithLiveBrands.Normalise(new ProductNameInput(
            "НИВЕА ГЕЛ ЗА БРИЧЕЊЕ 200МЛСЕНЗ.550302", Price: 299, UnitPrice: 149.50m, UnitBasis: "100ML"));

        Assert.Equal("200ML", r.Size?.Key);
        Assert.Equal(SizeSource.UnitPriceConfirmed, r.SizeSource);
        Assert.Equal("GEL ZA BRICHENJE SENZ", r.Descriptor);
        Assert.Equal("NIVEA|BRICHENJE GEL SENZ ZA|200ML", r.ExactKey);
    }

    [Fact]
    public void Kipper_whole_denar_unit_price_still_confirms_a_size()
    {
        // 149 den for 12 pieces is 12.42 den/pc; Kipper publishes "12".
        var r = N("OЛВЕЈС ПЛАТИНУМ НАЈТ 12К", "OLVEJS PLATINIUM NATË 12K", 149, 12m, "1PC");

        Assert.Equal("12PC", r.Size?.Key);
        Assert.Equal(SizeSource.UnitPriceConfirmed, r.SizeSource);
        Assert.Equal("OLVEJS PLATINUM NAJT", r.Descriptor); // the glued K went with the number
    }

    [Fact]
    public void Kipper_latin_lookalike_in_a_cyrillic_name_folds_cleanly() =>
        Assert.Equal("KRUGSEN KOPAR 30G", N("KРУГСЕН КОПАР 30г").Folded); // Latin K

    [Fact]
    public void Kipper_abbreviation_point_and_trailing_point()
    {
        var r = WithLiveBrands.Normalise(new ProductNameInput("АДА ДЕТ. ЗА САДОВИ ЈАБУКА 1000мл."));
        Assert.Equal("DET ZA SADOVI JABUKA", r.Descriptor);
        Assert.Equal("ADA|DET JABUKA SADOVI ZA|1000ML", r.ExactKey);
    }

    // ---- the case the whole rule set exists for ----

    [Fact]
    public void One_litre_and_one_and_a_half_litre_never_share_a_key() =>
        Assert.NotEqual(N("ЗДРАВЈЕ МЛЕКО 1Л").ExactKey, N("ЗДРАВЈЕ МЛЕКО 1,5Л").ExactKey);

    [Fact]
    public void Six_pack_and_single_bottle_never_share_a_key() =>
        Assert.NotEqual(N("ЗДРАВЈЕ ВОДА 6X0.5L").ExactKey, N("ЗДРАВЈЕ ВОДА 3L").ExactKey);

    [Fact]
    public void Cut_size_in_the_name_stays_in_the_descriptor()
    {
        var r = N("AGRINA POMFRIT 10X10MM 1KG");
        Assert.Equal("AGRINA|10X10MM POMFRIT|1000G", r.ExactKey);
    }
}

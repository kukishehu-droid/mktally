using Tally.Domain.Catalog;

namespace Tally.Domain.Tests.Catalog;

public class TransliteratorTests
{
    [Theory]
    [InlineData("ЖЕЛЕ 200 ГР", "ZHELE 200 GR")]
    [InlineData("ЌЕБАПИ ЃУБРЕ ЏЕМ ЅВЕЗДА ЉУБОВ ЊОКИ", "KJEBAPI GJUBRE DZHEM DZVEZDA LJUBOV NJOKI")]
    [InlineData("Чоколадо", "CHOKOLADO")]
    [InlineData("ТОРТ. ДОРА", "TORT. DORA")]
    [InlineData("МЛЕКО (1Л)", "MLEKO 1L")]
    public void Macedonian_cyrillic(string input, string expected) =>
        Assert.Equal(expected, Transliterator.Fold(input));

    [Theory]
    [InlineData("КАФE", "KAFE")]   // Latin E
    [InlineData("CОК", "SOK")]     // Latin C reads as Cyrillic С, not as C
    [InlineData("MЛEKO", "MLEKO")] // Latin M, E, K, O around Cyrillic Л
    public void Latin_lookalikes_inside_cyrillic_words(string input, string expected) =>
        Assert.Equal(expected, Transliterator.Fold(input));

    [Theory]
    [InlineData("6Х0,5Л", "6X0.5L")] // Cyrillic Х as multiplication sign
    [InlineData("6X0.5Л", "6X0.5L")] // Latin X next to a Cyrillic unit is left alone
    [InlineData("6 × 0,5 Л", "6X0.5 L")]
    [InlineData("1,5 Л", "1.5 L")]
    public void Multiplication_and_decimal_comma(string input, string expected) =>
        Assert.Equal(expected, Transliterator.Fold(input));

    [Theory]
    [InlineData("Çokollatë", "CHOKOLLATE")]
    [InlineData("Žito", "ZHITO")]           // ž folds like Cyrillic ж
    [InlineData("Šećer", "SHEKJER")]
    public void Latin_diacritics_fold_to_macedonian_digraphs(string input, string expected) =>
        Assert.Equal(expected, Transliterator.Fold(input));

    [Fact]
    public void Same_word_in_both_scripts_folds_equal() =>
        Assert.Equal(Transliterator.Fold("ЗДРАВЈЕ"), Transliterator.Fold("Zdravje"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty(string? input) => Assert.Equal("", Transliterator.Fold(input));
}

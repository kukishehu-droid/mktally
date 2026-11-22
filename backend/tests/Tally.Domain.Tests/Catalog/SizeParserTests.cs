using Tally.Domain.Catalog;

namespace Tally.Domain.Tests.Catalog;

public class SizeParserTests
{
    private static (PackSize? Size, bool Ambiguous, bool PiecesIgnored) Parse(string name) =>
        SizeParser.Resolve(SizeParser.FindAll(Transliterator.Fold(name)));

    [Theory]
    [InlineData("ЛЕБ ТОНУС 450 Г СТАНДАРД", "450G")]
    [InlineData("ЖЕЛЕ 200 ГР", "200G")]
    [InlineData("7ДЕЈС МИНИ КРОАСАН КАКАО 60г", "60G")]
    [InlineData("ЗДРАВЈЕ МЛЕКО 2.8% 1Л", "1000ML")]    // the % is not a size
    [InlineData("КОКА КОЛА 1,5Л", "1500ML")]
    [InlineData("ВОДА 0,5 Л", "500ML")]
    [InlineData("СОК 0.33L", "330ML")]
    [InlineData("ВИНО 75CL", "750ML")]
    [InlineData("ДОМАТИ 1 КГ", "1000G")]
    [InlineData("ЈАЈЦА 10 КОМ", "10PC")]
    [InlineData("AGRINA POMFRIT 10X10MM 1KG", "1000G")] // the cut size is not a package size
    public void Single_sizes(string name, string key)
    {
        var (size, ambiguous, _) = Parse(name);
        Assert.False(ambiguous);
        Assert.Equal(key, size?.Key);
    }

    [Theory]
    [InlineData("ПИВО 6Х0,5Л", "6X500ML", 3000)]
    [InlineData("ЈОГУРТ 4X125Г", "4X125G", 500)]
    [InlineData("ЧОКОЛАДО 100G X 3", "3X100G", 300)]
    [InlineData("ВОДА 6X0.5L 3L", "6X500ML", 3000)] // same total: the pack form wins
    public void Multipacks(string name, string key, int total)
    {
        var (size, _, _) = Parse(name);
        Assert.Equal(key, size?.Key);
        Assert.Equal(total, size?.Total);
    }

    [Theory]
    [InlineData("МАЛБОРО ГОЛД МЕКО")]
    [InlineData("КАФЕ 3LAYER")]
    [InlineData("7ДЕЈС КРОАСАН")]
    public void No_size(string name) => Assert.Null(Parse(name).Size);

    [Fact]
    public void Weight_beats_piece_count()
    {
        var (size, ambiguous, piecesIgnored) = Parse("ТОРТИЧКИ 6 КОМ 300Г");
        Assert.Equal("300G", size?.Key);
        Assert.False(ambiguous);
        Assert.True(piecesIgnored);
    }

    [Theory]
    [InlineData("СОК 1Л 500МЛ")]
    [InlineData("ПРОИЗВОД 500Г 1Л")]
    public void Disagreeing_sizes_are_ambiguous_and_yield_none(string name)
    {
        var (size, ambiguous, _) = Parse(name);
        Assert.Null(size);
        Assert.True(ambiguous);
    }

    [Theory]
    [InlineData("100G", 100, SizeDimension.Mass)]
    [InlineData("1KG", 1000, SizeDimension.Mass)]
    [InlineData("1L", 1000, SizeDimension.Volume)]
    [InlineData("100ML", 100, SizeDimension.Volume)]
    [InlineData("1PC", 1, SizeDimension.Count)]
    public void Unit_basis(string basis, int quantity, SizeDimension dimension)
    {
        var parsed = SizeParser.ParseBasis(basis);
        Assert.Equal((decimal)quantity, parsed?.Quantity);
        Assert.Equal(dimension, parsed?.Dimension);
    }
}

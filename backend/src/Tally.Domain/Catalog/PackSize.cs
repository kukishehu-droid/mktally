using System.Globalization;

namespace Tally.Domain.Catalog;

public enum SizeDimension
{
    Mass,    // grams
    Volume,  // millilitres
    Count,   // pieces
}

/// <summary>
/// A package size in base units (g, ml, pc). <c>6 × 0.5 L</c> is Count 6, Each 500, Volume.
/// </summary>
/// <remarks>
/// Size is a hard matching constraint (REQUIREMENTS.md §6.2 rule 1): two products match
/// only if their <see cref="Key"/>s are equal. A 6-pack of 0.5 L and a 3 L bottle have the
/// same <see cref="Total"/> but different keys, which is intended: same content, different SKU.
/// </remarks>
public readonly record struct PackSize(int Count, decimal Each, SizeDimension Dimension)
{
    /// <summary>Total content in base units, rounded.</summary>
    public int Total => (int)Math.Round(Count * Each, MidpointRounding.AwayFromZero);

    public string UnitCode => Dimension switch
    {
        SizeDimension.Mass => "g",
        SizeDimension.Volume => "ml",
        _ => "pc",
    };

    /// <summary><c>450G</c>, <c>1000ML</c>, <c>6X500ML</c>, <c>10PC</c>.</summary>
    public string Key
    {
        get
        {
            var each = Each.ToString("0.###", CultureInfo.InvariantCulture) + UnitCode.ToUpperInvariant();
            return Count > 1 ? $"{Count}X{each}" : each;
        }
    }

    public override string ToString() => Key;
}

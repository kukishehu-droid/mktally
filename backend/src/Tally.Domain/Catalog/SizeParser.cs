using System.Globalization;
using System.Text.RegularExpressions;

namespace Tally.Domain.Catalog;

/// <summary>A size found in a folded name, with where it was, so it can be cut out of the descriptor.</summary>
internal readonly record struct SizeMatch(PackSize Size, int Index, int Length);

/// <summary>
/// Finds package sizes in a name already folded by <see cref="Transliterator"/>.
/// Works on Latin only: "1Л" has become "1L", "450 Г" "450 G", "6Х0,5Л" "6X0.5L".
/// </summary>
internal static partial class SizeParser
{
    public static List<SizeMatch> FindAll(string folded)
    {
        var found = new List<SizeMatch>();

        // "125G X 4" first: the plain pattern would otherwise take "125G" and lose the count.
        foreach (var regex in new[] { PackAfterRegex(), PackFirstRegex() })
        {
            foreach (Match m in regex.Matches(folded))
            {
                if (found.Any(f => m.Index < f.Index + f.Length && f.Index < m.Index + m.Length))
                {
                    continue;
                }
                if (ToSize(m.Groups["n"], m.Groups["v"], m.Groups["u"]) is { } size)
                {
                    found.Add(new SizeMatch(size, m.Index, m.Length));
                }
            }
        }

        return found.OrderBy(f => f.Index).ToList();
    }

    /// <summary>
    /// Reduces candidates to one size, or none when they disagree.
    /// Mass or volume beats a piece count ("6 KOM 300G" is 300 g). Mass and volume
    /// together, or two different sizes of one dimension, are ambiguous, unless they
    /// describe the same total ("6X0.5L 3L"), in which case the pack form wins.
    /// </summary>
    public static (PackSize? Size, bool Ambiguous, bool PiecesIgnored) Resolve(IReadOnlyList<SizeMatch> candidates)
    {
        var distinct = candidates.Select(c => c.Size).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return (null, false, false);
        }

        var measured = distinct.Where(s => s.Dimension != SizeDimension.Count).ToList();
        var piecesIgnored = measured.Count > 0 && measured.Count < distinct.Count;
        var pool = measured.Count > 0 ? measured : distinct;

        if (pool.Select(s => s.Dimension).Distinct().Count() > 1)
        {
            return (null, true, piecesIgnored);
        }

        if (pool.Select(s => s.Total).Distinct().Count() > 1)
        {
            return (null, true, piecesIgnored);
        }

        return (pool.MaxBy(s => s.Count), false, piecesIgnored);
    }

    /// <summary>"100G", "1KG", "1L", "100ML", "1PC" (the <c>unit_basis</c> format) → quantity in base units.</summary>
    public static (decimal Quantity, SizeDimension Dimension)? ParseBasis(string? basis)
    {
        if (string.IsNullOrWhiteSpace(basis))
        {
            return null;
        }
        var m = BasisRegex().Match(basis.Trim().ToUpperInvariant());
        if (!m.Success || !Unit(m.Groups["u"].Value, out var factor, out var dim))
        {
            return null;
        }
        var qty = decimal.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) * factor;
        return qty > 0 ? (qty, dim) : null;
    }

    private static PackSize? ToSize(Group count, Group value, Group unit)
    {
        if (!Unit(unit.Value, out var factor, out var dim))
        {
            return null;
        }
        var v = decimal.Parse(value.Value, CultureInfo.InvariantCulture) * factor;
        var n = count.Success ? int.Parse(count.Value, CultureInfo.InvariantCulture) : 1;
        if (v <= 0 || n is < 1 or > 200)
        {
            return null;
        }
        return new PackSize(n, v, dim);
    }

    private static bool Unit(string token, out decimal factor, out SizeDimension dimension)
    {
        (factor, dimension) = token switch
        {
            "G" or "GR" or "GRAM" or "GRAMA" => (1m, SizeDimension.Mass),
            "KG" => (1000m, SizeDimension.Mass),
            "ML" => (1m, SizeDimension.Volume),
            "CL" => (10m, SizeDimension.Volume),
            "DL" => (100m, SizeDimension.Volume),
            "L" or "LT" or "LTR" or "LIT" or "LITAR" or "LITRI" => (1000m, SizeDimension.Volume),
            "KOM" or "PC" or "PCS" or "COPE" or "BR" => (1m, SizeDimension.Count),
            _ => (0m, SizeDimension.Count),
        };
        return factor > 0;
    }

    // Shared unit alternation. Longest first so "KG" is not read as "K" + "G".
    private const string Units = "KG|GRAMA|GRAM|GR|G|ML|CL|DL|LITAR|LITRI|LIT|LTR|LT|L|KOM|PCS|PC|COPE|BR";

    // [n X] value unit, e.g. "450G", "450 G", "1.5L", "6X0.5L", "4X125 GR".
    // Not preceded by a digit or point (so "1.5L" is not read as "5L"); a letter before is
    // allowed ("FIL280G"). Not followed by a letter, so "3LAYER" and "10MM" do not match.
    [GeneratedRegex(@"(?<![0-9.])(?:(?<n>\d{1,3})X)?(?<v>\d+(?:\.\d+)?)\s?(?<u>" + Units + @")(?![A-Z])")]
    private static partial Regex PackFirstRegex();

    // value unit X n, e.g. "125G X 4" (folded to "125GX4" or "125G X4").
    [GeneratedRegex(@"(?<![0-9.])(?<v>\d+(?:\.\d+)?)\s?(?<u>" + Units + @")\s?X\s?(?<n>\d{1,3})(?![0-9A-Z.])")]
    private static partial Regex PackAfterRegex();

    [GeneratedRegex(@"^(?<v>\d+(?:\.\d+)?)\s?(?<u>[A-Z]+)$")]
    private static partial Regex BasisRegex();
}

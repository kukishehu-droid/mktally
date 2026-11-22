using System.Globalization;
using System.Text.RegularExpressions;

namespace Tally.Domain.Catalog;

public enum BrandSource
{
    None,
    /// <summary>Found in the <see cref="BrandLexicon"/>. Trusted for the exact key.</summary>
    Lexicon,
    /// <summary>Guessed from the leading token. A hint for candidate generation only.</summary>
    LeadingToken,
}

public enum SizeSource
{
    None,
    /// <summary>Written with a unit in the primary (Macedonian) name.</summary>
    Name,
    /// <summary>Written with a unit in the alternative (Albanian) name only.</summary>
    AltName,
    /// <summary>A unitless number in the name ("ФИЛ280") agreed with price ÷ unit price.</summary>
    UnitPriceConfirmed,
    /// <summary>Worked out from price ÷ unit price alone. Not trusted for the exact key.</summary>
    UnitPriceInferred,
}

/// <summary>Reasons a record is kept out of deterministic matching. Stored in <c>source_product.norm_flags</c>.</summary>
public static class NormalisationFlags
{
    /// <summary>The name holds sizes that disagree ("1L 500ML").</summary>
    public const string SizeAmbiguous = "size_ambiguous";

    /// <summary>The Macedonian and Albanian names give different sizes.</summary>
    public const string SizeConflictsWithAlt = "size_conflict_alt";

    /// <summary>Price ÷ size is more than 10% away from the published unit price.</summary>
    public const string SizeUnitPriceMismatch = "size_unit_price_mismatch";

    /// <summary>Size came from price ÷ unit price, not from the name.</summary>
    public const string SizeInferred = "size_inferred";

    /// <summary>A piece count was dropped in favour of a weight or volume ("6 KOM 300G").</summary>
    public const string PiecesIgnored = "pieces_ignored";

    /// <summary>"2+1" style bundle; the price may not be for one unit.</summary>
    public const string Bundle = "bundle";
}

/// <param name="Name">Primary name, Macedonian (all chains).</param>
/// <param name="AltName">Albanian name (Kipper), if any. Used for brand and size when the primary lacks them.</param>
/// <param name="Price">Selling price, for the unit-price cross-check.</param>
/// <param name="UnitPrice">Published unit price, e.g. 16.66.</param>
/// <param name="UnitBasis">Its basis, e.g. "100G" (the <c>unit_basis</c> format).</param>
public sealed record ProductNameInput(
    string Name,
    string? AltName = null,
    int? Price = null,
    decimal? UnitPrice = null,
    string? UnitBasis = null);

/// <param name="Folded">The whole primary name, folded (<see cref="Transliterator.Fold"/>).</param>
/// <param name="Descriptor">Primary-name tokens minus lexicon brand and size, in original order.</param>
/// <param name="DescriptorKey">Same tokens, distinct and sorted, so word order does not matter across chains.</param>
/// <param name="ExactKey"><c>BRAND|DESCRIPTOR|SIZE</c>, or null when the record is not safe for exact matching.</param>
public sealed record NormalisedProduct(
    string Folded,
    string? Brand,
    BrandSource BrandSource,
    string Descriptor,
    string DescriptorKey,
    PackSize? Size,
    SizeSource SizeSource,
    string? ExactKey,
    IReadOnlyList<string> Flags);

/// <summary>
/// REQUIREMENTS.md §6.2 stage 0: deterministic, auditable, free. Pure; no I/O.
/// </summary>
/// <remarks>
/// The exact key exists only when every part of it is trustworthy: a lexicon brand, a size
/// read from the name (or a name number confirmed by the unit price), and no flag that casts
/// doubt on the size. Everything else waits for candidate generation and adjudication.
/// Precision over recall: a missed exact match costs an LLM call; a wrong one costs a wrong
/// savings figure.
/// </remarks>
public sealed partial class ProductNormaliser
{
    /// <summary>
    /// Bump when the rules change in a way that alters output, so stored rows
    /// (<c>source_product.norm_version</c>) can be re-normalised.
    /// </summary>
    public const int Version = 1;

    /// <summary>Price ÷ size may differ from the published unit price by this much (rounding in the source).</summary>
    private const decimal UnitPriceTolerance = 0.10m;

    /// <summary>A unitless number in the name confirms an inferred size within this margin.</summary>
    private const decimal ConfirmTolerance = 0.02m;

    private readonly BrandLexicon _brands;

    public ProductNormaliser(BrandLexicon brands) => _brands = brands;

    public NormalisedProduct Normalise(ProductNameInput input)
    {
        var flags = new List<string>();
        var folded = Transliterator.Fold(input.Name);
        var foldedAlt = Transliterator.Fold(input.AltName);

        if (BundleRegex().IsMatch(folded))
        {
            flags.Add(NormalisationFlags.Bundle);
        }

        // ---- size ----
        var primarySizes = SizeParser.FindAll(folded);
        var (size, ambiguous, piecesIgnored) = SizeParser.Resolve(primarySizes);
        var sizeSource = size is null ? SizeSource.None : SizeSource.Name;
        if (ambiguous)
        {
            flags.Add(NormalisationFlags.SizeAmbiguous);
        }

        if (foldedAlt.Length > 0)
        {
            var (altSize, altAmbiguous, _) = SizeParser.Resolve(SizeParser.FindAll(foldedAlt));
            if (size is null && !ambiguous && !altAmbiguous && altSize is not null)
            {
                size = altSize;
                sizeSource = SizeSource.AltName;
            }
            else if (size is { } p && altSize is { } a && p.Key != a.Key)
            {
                flags.Add(NormalisationFlags.SizeConflictsWithAlt);
            }
        }

        if (piecesIgnored)
        {
            flags.Add(NormalisationFlags.PiecesIgnored);
        }

        // ---- unit-price cross-check ----
        var basis = SizeParser.ParseBasis(input.UnitBasis);
        if (input.Price is > 0 && input.UnitPrice is > 0 && basis is { } b)
        {
            var inferred = input.Price.Value / input.UnitPrice.Value * b.Quantity; // base units

            if (size is { } s)
            {
                if (s.Dimension == b.Dimension && s.Total > 0
                    && Math.Abs(s.Total - inferred) / inferred > UnitPriceTolerance)
                {
                    flags.Add(NormalisationFlags.SizeUnitPriceMismatch);
                }
            }
            else if (!ambiguous)
            {
                if (FindConfirmingNumber(folded, input.Price.Value, input.UnitPrice.Value, b.Quantity) is { } confirmed)
                {
                    size = new PackSize(1, confirmed.Value, b.Dimension);
                    sizeSource = SizeSource.UnitPriceConfirmed;
                    // Cut the number from the descriptor, like a size: "FIL280" → "FIL", so it
                    // matches another chain's "ФИЛ 280Г". A unit glued after it goes too:
                    // "200MLSENZ" → "SENZ", "12K" → "".
                    var tail = folded[(confirmed.Index + confirmed.Length)..];
                    var unitPrefix = (b.Dimension switch
                    {
                        SizeDimension.Mass => MassUnitPrefixRegex(),
                        SizeDimension.Volume => VolumeUnitPrefixRegex(),
                        _ => CountUnitPrefixRegex(),
                    }).Match(tail);
                    primarySizes.Add(new SizeMatch(size.Value, confirmed.Index, confirmed.Length + unitPrefix.Length));
                }
                else
                {
                    size = new PackSize(1, Math.Round(inferred, 0, MidpointRounding.AwayFromZero), b.Dimension);
                    sizeSource = SizeSource.UnitPriceInferred;
                    flags.Add(NormalisationFlags.SizeInferred);
                }
            }
        }

        // ---- tokens, brand, descriptor ----
        var withoutSizes = CutSpans(folded, primarySizes);
        var tokens = Tokens(withoutSizes).ToList();

        string? brand = null;
        var brandSource = BrandSource.None;
        if (_brands.Find(tokens) is { } hit)
        {
            brand = hit.Brand;
            brandSource = BrandSource.Lexicon;
            tokens.RemoveRange(hit.Start, hit.Length);
        }
        else if (foldedAlt.Length > 0 && _brands.Find(Tokens(foldedAlt).ToList()) is { } altHit)
        {
            brand = altHit.Brand;
            brandSource = BrandSource.Lexicon;
        }
        else if (tokens.Count > 1 && IsBrandLike(tokens[0]))
        {
            brand = tokens[0];
            brandSource = BrandSource.LeadingToken; // kept in the descriptor: it may be a product word
        }

        var descriptor = string.Join(' ', tokens);
        var descriptorKey = string.Join(' ', tokens.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        var trustedSize = sizeSource is SizeSource.Name or SizeSource.AltName or SizeSource.UnitPriceConfirmed;
        var doubted = flags.Any(f => f is NormalisationFlags.SizeAmbiguous
            or NormalisationFlags.SizeConflictsWithAlt
            or NormalisationFlags.SizeUnitPriceMismatch
            or NormalisationFlags.Bundle);

        var exactKey = brandSource == BrandSource.Lexicon && size is { } k && trustedSize && !doubted
            ? $"{brand}|{descriptorKey}|{k.Key}"
            : null;

        return new NormalisedProduct(folded, brand, brandSource, descriptor, descriptorKey, size, sizeSource, exactKey, flags);
    }

    /// <summary>
    /// Splits a folded string into tokens. "ТОРТ." → "TORT"; a point inside a non-number
    /// splits it ("MLSENZ.550302" → "MLSENZ", "550302"); runs of five or more digits are
    /// dropped as supplier/article codes, which Misa appends to names and other chains do not.
    /// </summary>
    internal static IEnumerable<string> Tokens(string folded)
    {
        foreach (var raw in folded.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = NumberTokenRegex().IsMatch(raw) ? [raw] : raw.Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var t = NumberTokenRegex().IsMatch(part) ? part : part.Trim('/', '+');
                if (t.Length == 0 || !t.Any(char.IsLetterOrDigit) || ArticleCodeRegex().IsMatch(t))
                {
                    continue;
                }
                yield return t;
            }
        }
    }

    private static string CutSpans(string s, IReadOnlyList<SizeMatch> spans)
    {
        if (spans.Count == 0)
        {
            return s;
        }
        var chars = s.ToCharArray();
        foreach (var span in spans)
        {
            for (var i = span.Index; i < span.Index + span.Length; i++)
            {
                chars[i] = ' ';
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// A unitless number in the name that reproduces the published unit price, e.g. "FIL280":
    /// 89 ÷ 280 × 100 = 31.79 against 31.78 published. Compared on the unit price, within 2%
    /// or half a denar, because Kipper rounds unit prices to whole denars (149 den for 12
    /// pieces is published as 12, not 12.42).
    /// </summary>
    private static (decimal Value, int Index, int Length)? FindConfirmingNumber(
        string folded, int price, decimal unitPrice, decimal basisQuantity)
    {
        var tolerance = Math.Max(unitPrice * ConfirmTolerance, HalfDenar);
        foreach (Match m in BareNumberRegex().Matches(folded))
        {
            var n = decimal.Parse(m.Value, CultureInfo.InvariantCulture);
            if (n > 0 && Math.Abs(price / n * basisQuantity - unitPrice) <= tolerance)
            {
                return (n, m.Index, m.Length);
            }
        }
        return null;
    }

    /// <summary>Rounding slack for unit prices published as whole denars.</summary>
    private const decimal HalfDenar = 0.51m;

    private static bool IsBrandLike(string token) =>
        token.Length >= 3
        && token.All(char.IsLetter)
        && !GenericWords.Contains(token);

    /// <summary>
    /// Product words that often lead a name but are not brands. Folded forms.
    /// Extend from data: frequent leading tokens that span many unrelated products.
    /// </summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "LEB", "MLEKO", "JOGURT", "KISELO", "SIRENJE", "KASHKAVAL", "PUTER", "PAVLAKA", "KAJMAK",
        "KAFE", "CHAJ", "SOK", "VODA", "PIVO", "VINO", "RAKIJA", "CIGARI",
        "TORT", "TORTA", "TORTICHKI", "TORTICHKA", "ZHELE", "BONBONI", "CHOKOLADO", "BISKVITI",
        "KROASAN", "KIFLA", "KIFLI", "KEKS", "VAFLI", "GRICKALKI", "CHIPS", "SMOKI",
        "JAJCA", "DOMASHNI", "DOMASHNO", "DOMASHNA", "MESO", "PILESHKO", "PILESHKI", "SVINSKO",
        "GOVEDSKO", "SALAMA", "KOLBAS", "SUDZHUK", "SHUNKA", "PASHTETA", "RIBA", "TUNA",
        "MASLO", "OCET", "SHEKJER", "SOL", "BRASHNO", "ORIZ", "GRAV", "TESTENINI", "MAKARONI",
        "SPAGETI", "KECHAP", "MAJONEZ", "SENF", "AJVAR", "MED", "DZHEM", "MARMALAD",
        "DOMATI", "KOMPIRI", "KROMID", "JABOLKA", "BANANI", "PORTOKALI", "LIMONI",
        "DETERGENT", "OMEKNUVACH", "SAPUN", "SHAMPON", "PASTA", "TOALETNA", "HARTIJA", "SALVETI",
        "MINI", "MIDI", "MAKSI", "MAXI", "NOV", "NOVO", "NOVA", "AKCIJA", "PROMO", "SVEZH", "SVEZHO",
    };

    // "2+1", "1+1 GRATIS"
    [GeneratedRegex(@"(?<![0-9.])\d\s?\+\s?\d(?![0-9.])")]
    private static partial Regex BundleRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)?%?$")]
    private static partial Regex NumberTokenRegex();

    [GeneratedRegex(@"^\d{5,}$")]
    private static partial Regex ArticleCodeRegex();

    // A unit glued after a confirmed number ("200MLSENZ"), by dimension. Longest first.
    [GeneratedRegex(@"^(?:KG|GR|G)")]
    private static partial Regex MassUnitPrefixRegex();

    [GeneratedRegex(@"^(?:ML|CL|DL|L)")]
    private static partial Regex VolumeUnitPrefixRegex();

    // A lone K only when it ends the token ("12K"), so "12KAFE" keeps its K.
    [GeneratedRegex(@"^(?:KOM|PC|K(?![A-Z]))")]
    private static partial Regex CountUnitPrefixRegex();

    // Unitless numbers of 2-5 digits, glued to letters or not, not part of a decimal,
    // percentage or multiplication.
    [GeneratedRegex(@"(?<![0-9.])\d{2,5}(?![0-9.%X])")]
    private static partial Regex BareNumberRegex();
}

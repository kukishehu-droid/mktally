namespace Tally.Domain.Catalog;

/// <summary>
/// Known brands and their spellings across scripts ("7DAYS", "7 DAYS", "7ДЕЈС").
/// Loaded from the <c>brand</c> / <c>brand_alias</c> tables; grown by hand from the most
/// frequent unmatched leading tokens once the adapters have run.
/// </summary>
/// <remarks>
/// A brand found here is trusted enough to go into the exact match key. A brand guessed
/// from the leading token is not (see <see cref="BrandSource"/>).
/// </remarks>
public sealed class BrandLexicon
{
    // Folded alias tokens → canonical brand. Longest aliases are tried first.
    private readonly List<(string[] Tokens, string Brand)> _aliases;

    public BrandLexicon(IEnumerable<BrandEntry> entries)
    {
        _aliases = entries
            .SelectMany(e => e.Aliases.Append(e.Name).Select(a => (Tokens: Tokenise(a), Brand: Canonical(e.Name))))
            .Where(a => a.Tokens.Length > 0)
            .DistinctBy(a => string.Join(' ', a.Tokens))
            .OrderByDescending(a => a.Tokens.Length)
            .ThenByDescending(a => a.Tokens.Sum(t => t.Length))
            .ToList();
    }

    public static BrandLexicon Empty { get; } = new([]);

    public int Count => _aliases.Count;

    /// <summary>First (leftmost, then longest) alias occurring as a token sequence.</summary>
    internal (string Brand, int Start, int Length)? Find(IReadOnlyList<string> tokens)
    {
        (string Brand, int Start, int Length)? best = null;
        foreach (var (aliasTokens, brand) in _aliases)
        {
            for (var i = 0; i + aliasTokens.Length <= tokens.Count; i++)
            {
                if (best is { } b && i >= b.Start)
                {
                    break; // cannot beat an earlier match; longer aliases were tried first
                }
                if (Matches(tokens, i, aliasTokens))
                {
                    best = (brand, i, aliasTokens.Length);
                    break;
                }
            }
        }
        return best;
    }

    private static bool Matches(IReadOnlyList<string> tokens, int start, string[] alias)
    {
        for (var j = 0; j < alias.Length; j++)
        {
            if (!string.Equals(tokens[start + j], alias[j], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string Canonical(string name) => string.Join(' ', Tokenise(name));

    internal static string[] Tokenise(string text) =>
        ProductNormaliser.Tokens(Transliterator.Fold(text)).ToArray();
}

public sealed record BrandEntry(string Name, IReadOnlyList<string> Aliases);

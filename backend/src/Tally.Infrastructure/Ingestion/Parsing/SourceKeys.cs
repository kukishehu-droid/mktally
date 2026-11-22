using System.Security.Cryptography;
using System.Text;

namespace Tally.Infrastructure.Ingestion.Parsing;

/// <summary>
/// <c>source_product.source_key</c> builders (DATA-MODEL.md §4: "chain's own article code
/// or hashed name"). The prefix records which kind of key it is, so a chain that starts
/// publishing codes later does not collide with old name-hash keys.
/// </summary>
internal static class SourceKeys
{
    public static string ForCode(string code) => "code:" + code.Trim();

    /// <summary>Hash of the case- and whitespace-normalised name.</summary>
    public static string ForName(string name)
    {
        var normalised = (MandatedFieldParser.CleanText(name) ?? "").ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return "name:" + Convert.ToHexStringLower(hash)[..32];
    }
}

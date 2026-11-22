using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tally.Domain.Catalog;

/// <summary>
/// Folds a product name in Macedonian Cyrillic, Albanian or Latin into one uppercase
/// Latin form, so names from different chains and scripts can be compared.
/// REQUIREMENTS.md §6.2 stage 0.
/// </summary>
/// <remarks>
/// Steps, in order:
/// <list type="number">
/// <item>Unicode NFKC, uppercase.</item>
/// <item>A multiplication sign between digits (×, *, Latin X or Cyrillic Х) becomes <c>X</c>.</item>
/// <item>A decimal comma between digits becomes a point.</item>
/// <item>Latin look-alike letters inside a Cyrillic word become Cyrillic ("КАФE" typed with a
/// Latin E), so the word transliterates consistently.</item>
/// <item>Macedonian transliteration (ж→ZH, ш→SH, ч→CH, ќ→KJ, ѓ→GJ, џ→DZH, ѕ→DZ, љ→LJ, њ→NJ).
/// Latin letters with diacritics fold to the same digraphs (ž→ZH, š→SH, č→CH), Albanian ë→E.</item>
/// <item>Anything other than A–Z, 0–9, <c>. % / +</c> becomes a space; spaces collapse.</item>
/// </list>
/// Transliteration does not make "7ДЕЈС" equal "7DAYS"; that is the brand lexicon's job.
/// </remarks>
public static partial class Transliterator
{
    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['А'] = "A", ['Б'] = "B", ['В'] = "V", ['Г'] = "G", ['Д'] = "D", ['Ѓ'] = "GJ",
        ['Е'] = "E", ['Ж'] = "ZH", ['З'] = "Z", ['Ѕ'] = "DZ", ['И'] = "I", ['Ј'] = "J",
        ['К'] = "K", ['Л'] = "L", ['Љ'] = "LJ", ['М'] = "M", ['Н'] = "N", ['Њ'] = "NJ",
        ['О'] = "O", ['П'] = "P", ['Р'] = "R", ['С'] = "S", ['Т'] = "T", ['Ќ'] = "KJ",
        ['У'] = "U", ['Ф'] = "F", ['Х'] = "H", ['Ц'] = "C", ['Ч'] = "CH", ['Џ'] = "DZH",
        ['Ш'] = "SH",
        // Strays from Serbian/Bulgarian/Russian keyboards that turn up in POS data.
        ['Ђ'] = "DJ", ['Ћ'] = "KJ", ['Й'] = "J", ['Ы'] = "I", ['Э'] = "E", ['Ю'] = "JU",
        ['Я'] = "JA", ['Щ'] = "SHT", ['Ъ'] = "A", ['Ь'] = "", ['І'] = "I", ['Ё'] = "E",
    };

    // Latin letters whose diacritic carries a sound Macedonian writes as a digraph.
    private static readonly Dictionary<char, string> LatinDigraphs = new()
    {
        ['Ž'] = "ZH", ['Š'] = "SH", ['Č'] = "CH", ['Ć'] = "KJ", ['Đ'] = "DJ", ['Ç'] = "CH",
        ['Ǵ'] = "GJ", ['Ḱ'] = "KJ",
    };

    // Latin capitals that look identical to a Cyrillic capital.
    private static readonly Dictionary<char, char> LatinToCyrillicHomoglyph = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['C'] = 'С', ['E'] = 'Е', ['H'] = 'Н', ['K'] = 'К',
        ['M'] = 'М', ['O'] = 'О', ['P'] = 'Р', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
        ['J'] = 'Ј',
    };

    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var s = text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        s = MultiplyRegex().Replace(s, "X");
        s = DecimalCommaRegex().Replace(s, ".");
        s = FixHomoglyphs(s);

        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (Cyrillic.TryGetValue(ch, out var latin) || LatinDigraphs.TryGetValue(ch, out latin))
            {
                sb.Append(latin);
            }
            else
            {
                sb.Append(ch);
            }
        }

        // Strip remaining diacritics (ë → E, é → E).
        var decomposed = sb.ToString().Normalize(NormalizationForm.FormD);
        sb.Clear();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            sb.Append(ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '%' or '/' or '+' ? ch : ' ');
        }

        return SpacesRegex().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>
    /// Within each run of letters that contains at least one Cyrillic letter, map Latin
    /// look-alikes to Cyrillic. Runs are split by digits and punctuation, so the X in
    /// "6X0.5Л" is left alone.
    /// </summary>
    private static string FixHomoglyphs(string s)
    {
        return LetterRunRegex().Replace(s, run =>
        {
            var value = run.Value;
            if (!value.Any(IsCyrillic))
            {
                return value;
            }
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (LatinToCyrillicHomoglyph.TryGetValue(chars[i], out var cyr))
                {
                    chars[i] = cyr;
                }
            }
            return new string(chars);
        });
    }

    private static bool IsCyrillic(char ch) => ch is >= 'Ѐ' and <= 'ӿ';

    [GeneratedRegex(@"(?<=\d)\s*[×\*XХ]\s*(?=\d)")]
    private static partial Regex MultiplyRegex();

    [GeneratedRegex(@"(?<=\d),(?=\d)")]
    private static partial Regex DecimalCommaRegex();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex LetterRunRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}

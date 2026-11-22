using UglyToad.PdfPig;

namespace Tally.Infrastructure.Ingestion.Kam;

/// <summary>
/// PDF bytes → positioned glyphs per page, via PdfPig. The only place PdfPig is used, so the
/// table logic in <see cref="KamTableReader"/> stays independent of it.
/// </summary>
/// <remarks>
/// KAM's files are Crystal Reports output: text-based, two subset TrueType fonts (Cyrillic
/// letters in one, Latin, digits and punctuation in the other) with ToUnicode maps, spaces
/// drawn as glyphs. Verified on the live Struga files 2026-11-22.
/// </remarks>
internal static class KamPdfReader
{
    /// <exception cref="KamFormatException">PdfPig cannot open the file.</exception>
    public static IReadOnlyList<IReadOnlyList<KamGlyph>> Read(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var pages = new List<IReadOnlyList<KamGlyph>>(document.NumberOfPages);
            foreach (var page in document.GetPages())
            {
                pages.Add(page.Letters
                    .Select(l => new KamGlyph(l.Value, l.StartBaseLine.X, l.EndBaseLine.X, l.StartBaseLine.Y, l.PointSize))
                    .ToList());
            }
            return pages;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not KamFormatException)
        {
            throw new KamFormatException($"PdfPig could not read the price list ({ex.GetType().Name}: {ex.Message}).", ex);
        }
    }
}

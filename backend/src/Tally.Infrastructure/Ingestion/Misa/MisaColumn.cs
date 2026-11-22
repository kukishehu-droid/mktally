namespace Tally.Infrastructure.Ingestion.Misa;

/// <summary>
/// The legally mandated columns as Misa labels them (REQUIREMENTS.md §4.3).
/// </summary>
internal enum MisaColumn
{
    Name,            // Назив на стока-производ
    Price,           // Продажна цена
    UnitPrice,       // Единечна цена
    Description,     // Опис на стока
    Availability,    // Достапност во продажен објект
    RegularPrice,    // Редовна цена
    PromoPrice,      // Промотивна или попустна цена
    DiscountedPrice, // Цена со попуст
    DiscountPct,     // Попуст(%) - KAM has it; Misa's header list does not, mapped if it appears
    PromoType,       // Вид на продажно поттикнување
    PromoPeriod,     // Времетраење на промоција
}

/// <summary>
/// Maps cell positions to columns by reading the header row. Rows are ragged (trailing
/// promo columns are omitted when there is no promotion), so mapping by header text is
/// safer than fixed positions.
/// </summary>
internal sealed class MisaColumnMap
{
    /// <summary>Order documented on 2026-11-20. Used only when the table has no header row.</summary>
    public static readonly MisaColumnMap Documented = new(
    [
        MisaColumn.Name, MisaColumn.Price, MisaColumn.UnitPrice, MisaColumn.Description,
        MisaColumn.Availability, MisaColumn.RegularPrice, MisaColumn.PromoPrice,
        MisaColumn.DiscountedPrice, MisaColumn.PromoType, MisaColumn.PromoPeriod,
    ]);

    private readonly MisaColumn?[] _byIndex;

    private MisaColumnMap(MisaColumn?[] byIndex) => _byIndex = byIndex;

    public MisaColumn? this[int index] => index < _byIndex.Length ? _byIndex[index] : null;

    /// <exception cref="MisaFormatException">
    /// Headers are present but the name or selling-price column cannot be found, which means
    /// the page layout changed. Failing loudly beats silently mapping prices to the wrong field.
    /// </exception>
    public static MisaColumnMap FromHeaders(IReadOnlyList<string> headers)
    {
        if (headers.Count == 0)
        {
            return Documented;
        }

        var map = headers.Select(Classify).ToArray();
        if (!map.Contains(MisaColumn.Name) || !map.Contains(MisaColumn.Price))
        {
            throw new MisaFormatException(
                $"Price table headers not recognised: [{string.Join(" | ", headers)}]");
        }

        return new MisaColumnMap(map);
    }

    internal static MisaColumn? Classify(string header)
    {
        var h = header.Trim().ToLowerInvariant();

        // Order matters: "цена со попуст" must be tested before the bare "попуст".
        if (h.StartsWith("назив")) return MisaColumn.Name;
        if (h.StartsWith("продажна")) return MisaColumn.Price;
        if (h.StartsWith("единечна") || h.StartsWith("единична")) return MisaColumn.UnitPrice;
        if (h.StartsWith("опис")) return MisaColumn.Description;
        if (h.StartsWith("достапност")) return MisaColumn.Availability;
        if (h.StartsWith("редовна")) return MisaColumn.RegularPrice;
        if (h.StartsWith("промотивна")) return MisaColumn.PromoPrice;
        if (h.StartsWith("цена со попуст")) return MisaColumn.DiscountedPrice;
        if (h.StartsWith("попуст")) return MisaColumn.DiscountPct;
        if (h.StartsWith("вид")) return MisaColumn.PromoType;
        if (h.StartsWith("времетраење")) return MisaColumn.PromoPeriod;
        return null;
    }
}

public sealed class MisaFormatException(string message) : Exception(message);

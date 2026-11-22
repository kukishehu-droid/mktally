using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Misa;

/// <summary>
/// One Misa row → <see cref="RawPriceRecord"/>. Pure; no I/O.
/// </summary>
internal static class MisaRecordMapper
{
    public static bool TryMap(MisaRow row, out RawPriceRecord record, out string? skipReason)
    {
        record = null!;

        var name = row[MisaColumn.Name];
        if (name is null)
        {
            skipReason = "missing name";
            return false;
        }

        var price = MandatedFieldParser.ParseDenars(row[MisaColumn.Price]);
        if (price is null or <= 0)
        {
            skipReason = $"unparseable selling price '{row[MisaColumn.Price]}'";
            return false;
        }

        var regular = MandatedFieldParser.ParseDenars(row[MisaColumn.RegularPrice]);

        // Verified 2026-11-22: "Промотивна или попустна цена" is a group heading over three
        // columns, and the first of them, "Цена со попуст", holds both values in one cell:
        // "299 ден. Попуст:3%". PromoPrice is kept for a layout without the sub-headers.
        int? promo = null;
        decimal? pct = MandatedFieldParser.ParsePercent(row[MisaColumn.DiscountPct]);

        foreach (var cell in new[] { row[MisaColumn.DiscountedPrice], row[MisaColumn.PromoPrice] })
        {
            var (denars, percent) = MandatedFieldParser.ParsePriceAndPercent(cell);
            promo ??= denars;
            pct ??= percent;
        }

        // A promo equal to the regular price is no promo.
        if (promo is not null && regular is not null && promo >= regular)
        {
            promo = null;
        }

        // Selling below the regular price is a promotion even if no promo column was filled.
        // Flagging it matters: promo prices expire (CLAUDE.md).
        if (promo is null && regular is not null && price < regular)
        {
            promo = price;
        }

        if (pct is null && promo is not null && regular is > 0)
        {
            pct = Math.Round((regular.Value - promo.Value) * 100m / regular.Value, 2);
        }

        var unit = MandatedFieldParser.ParseUnitPrice(row[MisaColumn.UnitPrice]);
        var (promoFrom, promoTo) = MandatedFieldParser.ParsePromoWindow(row[MisaColumn.PromoPeriod]);

        record = new RawPriceRecord
        {
            SourceKey = SourceKeyFor(name),
            NamePrimary = name,
            NameAlt = null,
            Category = row[MisaColumn.Description],
            Price = price.Value,
            RegularPrice = regular,
            PromoPrice = promo,
            DiscountPct = promo is null ? null : pct,
            UnitPrice = unit?.Price,
            UnitBasis = unit?.Basis,
            IsAvailable = MandatedFieldParser.ParseAvailability(row[MisaColumn.Availability]),
            PromoType = promo is null ? null : row[MisaColumn.PromoType],
            PromoFrom = promo is null ? null : promoFrom,
            PromoTo = promo is null ? null : promoTo,
            // Misa's page states no publication date (as far as verified). Null makes the
            // ingest date the displayed date, which is honest.
            PublishedOn = null,
        };
        skipReason = null;
        return true;
    }

    /// <summary>
    /// Misa publishes no article code, so the key is a hash of the normalised name.
    /// The raw name is kept in <c>raw_name_primary</c>.
    /// </summary>
    internal static string SourceKeyFor(string name) => SourceKeys.ForName(name);
}

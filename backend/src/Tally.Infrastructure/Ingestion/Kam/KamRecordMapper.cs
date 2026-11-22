using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Kam;

/// <summary>
/// One KAM table row → <see cref="RawPriceRecord"/>. Pure; no I/O. The cell formats are the
/// mandated ones Misa uses too ("102ден.", "102ден. Попуст: 11%", "16.11.2026 до 22.11.2026"),
/// so parsing goes through <see cref="MandatedFieldParser"/>.
/// </summary>
internal static class KamRecordMapper
{
    public static bool TryMap(KamRow row, DateOnly publishedOn, out RawPriceRecord record, out string? skipReason)
    {
        record = null!;

        var name = row[KamColumn.Name];
        if (name is null)
        {
            skipReason = "missing name";
            return false;
        }

        var price = MandatedFieldParser.ParseDenars(row[KamColumn.Price]);
        if (price is null or <= 0)
        {
            skipReason = $"unparseable selling price '{row[KamColumn.Price]}'";
            return false;
        }

        var regular = MandatedFieldParser.ParseDenars(row[KamColumn.RegularPrice]);

        // "Цена со попуст" holds the promo price with "Попуст: 11%" beneath it, in one cell.
        var (promo, pct) = MandatedFieldParser.ParsePriceAndPercent(row[KamColumn.DiscountedPrice]);

        // A promo at or above the regular price is no promo.
        if (promo is not null && regular is not null && promo >= regular)
        {
            promo = null;
        }

        // Selling below the regular price is a promotion even if the promo cell is empty.
        // Flagging it matters: promo prices expire (CLAUDE.md).
        if (promo is null && regular is not null && price < regular)
        {
            promo = price;
        }

        // Live 2026-11-22: 15 rows in store 38 had "промотивна цена", a promo price and a
        // window but no regular price. The promo flag stays; the percentage stays unknown.
        if (pct is null && promo is not null && regular is > 0)
        {
            pct = Math.Round((regular.Value - promo.Value) * 100m / regular.Value, 2);
        }

        var unit = CorrectUnit(MandatedFieldParser.ParseUnitPrice(row[KamColumn.UnitPrice]));
        var (promoFrom, promoTo) = MandatedFieldParser.ParsePromoWindow(row[KamColumn.PromoPeriod]);

        record = new RawPriceRecord
        {
            SourceKey = SourceKeys.ForName(name),
            NamePrimary = name,
            // "Опис на стока" usually starts with the brand in Latin ("GRIESSON БИСКВИТ ..."),
            // which the name column abbreviates or omits. The normaliser looks for it here.
            NameAlt = row[KamColumn.Description],
            Category = null, // KAM publishes no category
            Price = price.Value,
            RegularPrice = regular,
            PromoPrice = promo,
            DiscountPct = promo is null ? null : pct,
            UnitPrice = unit?.Price,
            UnitBasis = unit?.Basis,
            IsAvailable = MandatedFieldParser.ParseAvailability(row[KamColumn.Availability]),
            PromoType = promo is null ? null : row[KamColumn.PromoType],
            PromoFrom = promo is null ? null : promoFrom,
            PromoTo = promo is null ? null : promoTo,
            PublishedOn = publishedOn,
        };
        skipReason = null;
        return true;
    }

    /// <summary>
    /// KAM's unit-price labels are systematically wrong in two ways and nonsensical in two
    /// more. Measured on store 38's 1,836 rows on 2026-11-22:
    /// <list type="bullet">
    /// <item>"1 гр" (146 rows) is the price per <b>kilogram</b>: "ДУО КЕКС ГРИСОН 500ГР",
    /// 102 den, "1 гр = 204.00 ден.". 137 of 146 show a unit price above the pack price,
    /// impossible per gram.</item>
    /// <item>"1 мл" (182 rows) is the price per <b>litre</b>: "МАСЛО БРИЛИЈАНТ 5Л", 499 den,
    /// "1 мл = 99.80 ден.".</item>
    /// <item>"100 кг" (2 rows) and "100 пар" (15 rows) describe a 100 g price under a wrong
    /// unit. The basis cannot be trusted, so the unit price is dropped.</item>
    /// </list>
    /// Anything outside the bases seen correct is dropped too: a unit price on a wrong basis is
    /// worse than none (normalisation vetoes matches on it). "100 мл" on solid goods (a 400 g
    /// yoghurt) is left as published; the normaliser's unit-price check sees that.
    /// </summary>
    internal static (decimal Price, string Basis)? CorrectUnit((decimal Price, string Basis)? unit) =>
        unit switch
        {
            null => null,
            (var p, "1G") => (p, "1KG"),
            (var p, "1ML") => (p, "1L"),
            (_, "1KG" or "1L" or "100G" or "100ML" or "1PC") => unit,
            _ => null,
        };
}

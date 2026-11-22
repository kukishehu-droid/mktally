namespace Tally.Application.Ingestion;

/// <summary>
/// The common shape every chain normalises into. One record = one product at one store
/// on one publication. REQUIREMENTS.md §5.1; columns follow <c>price_observation</c> and
/// <c>source_product</c> in DATA-MODEL.md §4.
/// </summary>
/// <remarks>
/// Money is whole denars (<see cref="int"/>). Adapters do source-format parsing only
/// (strip "ден.", read "Да"/"Не", split promo windows). Brand/size normalisation is a
/// later pipeline stage and does not belong here.
/// </remarks>
public sealed record RawPriceRecord
{
    /// <summary>
    /// Stable per-chain product key: the chain's article code, or a hash of the
    /// normalised name when the chain publishes none. Maps to <c>source_product.source_key</c>.
    /// </summary>
    public required string SourceKey { get; init; }

    /// <summary>Macedonian name, as published.</summary>
    public required string NamePrimary { get; init; }

    /// <summary>Albanian name (Kipper) or description. Null when the chain has none.</summary>
    public string? NameAlt { get; init; }

    /// <summary>The chain's own category / description string.</summary>
    public string? Category { get; init; }

    /// <summary>Selling price, whole denars. What the customer pays today.</summary>
    public required int Price { get; init; }

    public int? RegularPrice { get; init; }

    /// <summary>Set only when the selling price is a promotion. Drives the promo flag in the UI.</summary>
    public int? PromoPrice { get; init; }

    public decimal? DiscountPct { get; init; }

    public decimal? UnitPrice { get; init; }

    /// <summary><c>1KG</c>, <c>1L</c>, <c>100G</c>, <c>100ML</c>, <c>1PC</c>. Null when not published or not understood.</summary>
    public string? UnitBasis { get; init; }

    /// <summary>KAM and Misa publish this; Kipper does not (null).</summary>
    public bool? IsAvailable { get; init; }

    public string? PromoType { get; init; }
    public DateTimeOffset? PromoFrom { get; init; }
    public DateTimeOffset? PromoTo { get; init; }

    /// <summary>
    /// The date the chain says the list is for. Null when the source does not state one;
    /// the ingest date (<c>observed_on</c>) is then the only date shown.
    /// </summary>
    public DateOnly? PublishedOn { get; init; }
}

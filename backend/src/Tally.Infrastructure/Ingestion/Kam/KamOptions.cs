namespace Tally.Infrastructure.Ingestion.Kam;

/// <summary>
/// KAM: one JSON store list, one PDF price list per store. REQUIREMENTS.md §4.2.
/// Everything here was checked against the live site on 2026-11-22.
/// </summary>
public sealed class KamOptions
{
    public const string SectionName = "Crawlers:Kam";

    public Uri BaseUrl { get; set; } = new("https://kam.com.mk/");

    /// <summary>POST with an empty body. Returns a JSON array of all 85 stores.</summary>
    public string ShopListPath { get; set; } = "ShopsWeb/LoadShopList";

    /// <summary>
    /// Stores whose <c>City</c> matches one of these are in scope. Filter on <c>City</c>,
    /// never <c>Name</c>: "Генц" is in Struga without saying so (REQUIREMENTS.md §4.4).
    /// Left empty, <see cref="DefaultCities"/> is used.
    /// </summary>
    public List<string> Cities { get; set; } = [];

    public static IReadOnlyList<string> DefaultCities { get; } = ["Струга"];

    /// <summary>
    /// The store list carries every store's price-list path, so one download serves the probes
    /// of all stores in a run. A no-change night then costs one request for the whole chain.
    /// </summary>
    public TimeSpan ShopListCacheFor { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Wait before each PDF download.</summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RequestDelayJitter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Struga's lists are about 0.5 MB (161-167 pages). Anything far larger is not a price list.</summary>
    public int MaxPdfBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Share of non-blank glyphs the table reader could not place in a row and column. Above
    /// this the layout has changed and the run fails rather than ingest half-read rows.
    /// The live Struga lists had none.
    /// </summary>
    public double MaxOrphanShare { get; set; } = 0.005;

    /// <summary>A snapshot older than this is still ingested (it is what KAM publishes) but logged as a warning.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Ten x positions (PDF points) of the table's vertical rules, left to right. Left empty,
    /// <see cref="KamTableLayout.DefaultColumnEdges"/> (measured 2026-11-22) is used. The reader
    /// checks each header label falls in its column, so a moved column fails loudly.
    /// </summary>
    public List<double> ColumnEdges { get; set; } = [];
}

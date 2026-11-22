namespace Tally.Infrastructure.Ingestion.Kipper;

/// <summary>
/// Request shape verified on 2026-11-22 against the live store 131 page:
/// <c>POST /wp-admin/admin-ajax.php</c> with <c>action=get_products_data</c> and
/// <c>post_id={WordPress post id of the store page}</c>, plus the standard DataTables
/// <c>draw/start/length</c>. No nonce. <c>length=500</c> is honoured.
/// </summary>
public sealed class KipperOptions
{
    public const string SectionName = "Crawlers:Kipper";

    public Uri BaseUrl { get; set; } = new("https://kipper.mk/");

    public string AjaxPath { get; set; } = "wp-admin/admin-ajax.php";

    public string AjaxAction { get; set; } = "get_products_data";

    /// <summary>Form field naming the store: the store page's WordPress post id, not the store number.</summary>
    public string StoreParamName { get; set; } = "post_id";

    /// <summary>Extra form fields sent with every AJAX request. None are needed today.</summary>
    public Dictionary<string, string> ExtraFormFields { get; set; } = new();

    /// <summary>Rows per request. Store 131 has 2,190 rows, so five requests per store.</summary>
    public int PageSize { get; set; } = 500;

    public int MaxPages { get; set; } = 50;

    /// <summary>The site sits behind Cloudflare: keep requests spaced out.</summary>
    public TimeSpan PageDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan PageDelayJitter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Roster override. Left empty, <see cref="KipperStoreRoster.Struga"/> is used.</summary>
    public List<KipperStoreOptions> Stores { get; set; } = [];
}

public sealed class KipperStoreOptions
{
    /// <summary>Kipper's store number, e.g. "131". Becomes <c>store.external_id</c>.</summary>
    public string StoreId { get; set; } = "";

    /// <summary>Store page path relative to <see cref="KipperOptions.BaseUrl"/>.</summary>
    public string PagePath { get; set; } = "";

    /// <summary>
    /// WordPress post id sent as <see cref="KipperOptions.StoreParamName"/>. Normally read from
    /// the store page (<c>postid-NNNNN</c> body class); set only to override.
    /// </summary>
    public string? PostId { get; set; }

    public string Name { get; set; } = "";
    public string? City { get; set; }
}

/// <summary>
/// Kipper's Struga stores, from the site's sitemap on 2026-11-22. Three, not the two in
/// REQUIREMENTS.md §4.4: store 118 (Дебарски пат) was missing there.
/// </summary>
public static class KipperStoreRoster
{
    public static IReadOnlyList<KipperStoreOptions> Struga { get; } =
    [
        new() { StoreId = "131", PagePath = "mk/kipper-131struge/",  Name = "Кипер 131", City = "Струга" },
        new() { StoreId = "084", PagePath = "mk/kipper-84-struge/",  Name = "Кипер 084", City = "Струга" },
        new() { StoreId = "118", PagePath = "mk/kipper-118-struge/", Name = "Кипер 118", City = "Струга" },
    ];
}

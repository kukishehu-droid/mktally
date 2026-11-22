using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Http;
using Tally.Infrastructure.Ingestion.Kipper;

namespace Tally.Infrastructure.Tests.Ingestion;

public class KipperAdapterTests
{
    private static readonly StoreDescriptor Store131 = new() { ChainCode = "kipper", ExternalId = "131", Name = "Кипер 131" };

    // Shape of the live store page (2026-11-22): postid body class, server-rendered stamp.
    private const string StorePage = """
        <html><head><title>Kipper 131,Strugë | Kipper Market</title></head>
        <body class="single single-post postid-22911 single-format-standard">
        <a href="https://www.google.com/maps/@41.1754493,20.6745462,17.75z?entry=ttu">Прати ме до маркет</a>
        <div class="elementor-shortcode">Датум и време на последно ажурирање на цените: 20 November 2026 - 15:43<table id="products"></table></div>
        </body></html>
        """;

    [Fact]
    public async Task Fetch_pages_through_the_table_using_records_filtered()
    {
        var server = new FakeKipper(totalRows: 7, serverCap: 3);
        var records = await Adapter(server).FetchAsync(Store131, CancellationToken.None);

        Assert.Equal(7, records.Count);
        Assert.Equal(3, server.AjaxRequests); // 3 + 3 + 1, server caps below PageSize
        Assert.All(records, r => Assert.Equal(new DateOnly(2026, 11, 20), r.PublishedOn));
    }

    [Fact]
    public async Task Fetch_sends_the_verified_minimal_request()
    {
        var server = new FakeKipper(totalRows: 2);
        await Adapter(server).FetchAsync(Store131, CancellationToken.None);

        Assert.Equal(new Dictionary<string, string>
        {
            ["action"] = "get_products_data",
            ["post_id"] = "22911", // read from the store page, not the store number
            ["draw"] = "1",
            ["start"] = "0",
            ["length"] = "500",
        }, server.LastForm);
    }

    [Fact]
    public async Task Extra_form_fields_are_sent()
    {
        var server = new FakeKipper(totalRows: 1);
        await Adapter(server, o => o.ExtraFormFields["columns[0][data]"] = "product_name")
            .FetchAsync(Store131, CancellationToken.None);
        Assert.Equal("product_name", server.LastForm!["columns[0][data]"]);
    }

    [Fact]
    public async Task Configured_post_id_overrides_the_page()
    {
        var server = new FakeKipper(totalRows: 1);
        await Adapter(server, o => o.Stores =
            [new KipperStoreOptions { StoreId = "131", PagePath = "mk/kipper-131struge/", PostId = "99999", Name = "Кипер 131" }])
            .FetchAsync(Store131, CancellationToken.None);
        Assert.Equal("99999", server.LastForm!["post_id"]);
    }

    [Fact]
    public async Task Fetch_fails_clearly_without_a_post_id()
    {
        var server = new FakeKipper(totalRows: 1) { StorePageHtml = "<html><body>Кипер</body></html>" };
        var ex = await Assert.ThrowsAsync<KipperFormatException>(() =>
            Adapter(server).FetchAsync(Store131, CancellationToken.None));
        Assert.Contains("postid", ex.Message);
        Assert.Equal(0, server.AjaxRequests);
    }

    [Fact]
    public async Task Fetch_fails_clearly_when_wordpress_rejects_the_action()
    {
        var server = new FakeKipper(totalRows: 1) { RawAjaxBody = "0" };
        var ex = await Assert.ThrowsAsync<KipperFormatException>(() =>
            Adapter(server).FetchAsync(Store131, CancellationToken.None));
        Assert.Contains("AjaxAction", ex.Message);
    }

    [Fact]
    public async Task Fetch_fails_when_the_table_is_empty()
    {
        var server = new FakeKipper(totalRows: 0);
        await Assert.ThrowsAsync<KipperFormatException>(() =>
            Adapter(server).FetchAsync(Store131, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_uses_the_last_update_timestamp_and_reports_publication_date()
    {
        var fp = await Adapter(new FakeKipper(1)).ProbeAsync(Store131, CancellationToken.None);

        Assert.Equal("updated:2026-11-20T14:43:00.0000000+00:00", fp.Value);
        Assert.Equal(new DateOnly(2026, 11, 20), fp.PublishedOn);
    }

    [Fact]
    public async Task Probe_without_timestamp_forces_a_fetch()
    {
        var server = new FakeKipper(1) { StorePageHtml = "<html><body>no date</body></html>" };
        var a = await Adapter(server).ProbeAsync(Store131, CancellationToken.None);
        await Task.Delay(5);
        var b = await Adapter(server).ProbeAsync(Store131, CancellationToken.None);

        Assert.StartsWith("unknown:", a.Value);
        Assert.NotEqual(a.Value, b.Value);
    }

    [Fact]
    public async Task Discover_reads_coordinates_from_the_store_page()
    {
        var stores = await Adapter(new FakeKipper(1)).DiscoverStoresAsync(CancellationToken.None);

        Assert.Equal(new[] { "131", "084", "118" }, stores.Select(s => s.ExternalId));
        Assert.Equal(41.1754493, stores[0].Latitude);
    }

    [Fact]
    public async Task Cloudflare_challenge_is_reported_not_retried()
    {
        var server = new FakeKipper(1) { Challenge = true };
        await Assert.ThrowsAsync<CrawlBlockedException>(() =>
            Adapter(server).ProbeAsync(Store131, CancellationToken.None));
        Assert.Equal(1, server.PageRequests);
    }

    // Abridged from kipper.mk/robots.txt on 2026-11-22: a Cloudflare content-signal group,
    // named AI crawlers blocked, then the WordPress defaults with CRLF line endings.
    private const string LiveRobots =
        "User-agent: *\nContent-Signal: search=yes,ai-train=no,use=reference\nAllow: /\n" +
        "User-agent: ClaudeBot\nDisallow: /\nUser-agent: GPTBot\nDisallow: /\n" +
        "User-agent: *\r\nDisallow: /wp-admin/\r\nDisallow: /wp-includes/\r\nAllow: /wp-admin/admin-ajax.php\r\n" +
        "Sitemap: https://kipper.mk/wp-sitemap.xml";

    [Fact]
    public async Task Live_robots_allows_the_store_page_and_admin_ajax()
    {
        var server = new FakeKipper(totalRows: 1) { Robots = LiveRobots };
        var records = await Adapter(server).FetchAsync(Store131, CancellationToken.None);
        Assert.Single(records);
    }

    // ---- helpers ----

    private static KipperAdapter Adapter(FakeKipper server, Action<KipperOptions>? configure = null)
    {
        var options = new KipperOptions { PageDelay = TimeSpan.Zero, PageDelayJitter = TimeSpan.Zero };
        configure?.Invoke(options);
        return new KipperAdapter(TestHttp.Polite(server), Options.Create(options), TimeProvider.System,
            NullLogger<KipperAdapter>.Instance);
    }

    /// <summary>Store page on GET, DataTables on POST admin-ajax.php.</summary>
    private sealed class FakeKipper(int totalRows, int serverCap = int.MaxValue) : HttpMessageHandler
    {
        public string StorePageHtml { get; init; } = StorePage;
        public string? RawAjaxBody { get; init; }
        public string? Robots { get; init; }
        public bool Challenge { get; init; }

        public int PageRequests { get; private set; }
        public int AjaxRequests { get; private set; }
        public Dictionary<string, string>? LastForm { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/robots.txt")
            {
                return Robots is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Robots) };
            }

            if (request.Method == HttpMethod.Get)
            {
                PageRequests++;
                if (Challenge)
                {
                    var blocked = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    blocked.Headers.Add("cf-mitigated", "challenge");
                    return blocked;
                }
                return Text(StorePageHtml, "text/html");
            }

            AjaxRequests++;
            var body = await request.Content!.ReadAsStringAsync(ct);
            LastForm = body.Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));

            if (RawAjaxBody is not null)
            {
                return Text(RawAjaxBody, "text/html");
            }

            var start = int.Parse(LastForm["start"]);
            var length = Math.Min(int.Parse(LastForm["length"]), serverCap);
            var rows = Enumerable.Range(start, Math.Max(0, Math.Min(length, totalRows - start)))
                .Select(i => new
                {
                    product_image = $"{i:000000}.webp",
                    product_name = $"АРТИКЛ {i}",
                    product_price = (100 + i).ToString(),
                    product_price_normal = (100 + i).ToString(),
                });
            var json = JsonSerializer.Serialize(new
            {
                draw = int.Parse(LastForm["draw"]),
                recordsTotal = totalRows,
                recordsFiltered = totalRows,
                data = rows,
            });
            return Text(json, "application/json");
        }

        private static HttpResponseMessage Text(string body, string mediaType) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }
}

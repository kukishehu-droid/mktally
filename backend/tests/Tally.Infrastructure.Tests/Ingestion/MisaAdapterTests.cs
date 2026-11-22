using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Http;
using Tally.Infrastructure.Ingestion.Misa;

namespace Tally.Infrastructure.Tests.Ingestion;

public class MisaAdapterTests
{
    private static readonly StoreDescriptor Store = new() { ChainCode = "misa", ExternalId = "3", Name = "Пазар" };

    [Fact]
    public async Task Fetch_walks_pages_until_the_last_linked_page()
    {
        var handler = new FakeHandler(uri => PageNumber(uri) switch
        {
            1 => Html(["А", "Б"], linkedPages: [1, 2, 3]),
            2 => Html(["В", "Г"], linkedPages: [1, 2, 3]),
            3 => Html(["Д"], linkedPages: [1, 2, 3]),
            _ => throw new InvalidOperationException("should not request past the last page"),
        });

        var records = await Adapter(handler).FetchAsync(Store, CancellationToken.None);

        Assert.Equal(new[] { "А", "Б", "В", "Г", "Д" }, records.Select(r => r.NamePrimary));
        Assert.Equal(3, handler.PageRequests);
    }

    [Fact]
    public async Task Fetch_stops_when_site_clamps_out_of_range_page_to_last_page()
    {
        // No pagination links, full pages, and page 3 repeats page 2.
        var handler = new FakeHandler(uri => PageNumber(uri) switch
        {
            1 => Html(["А", "Б"]),
            _ => Html(["В", "Г"]),
        });

        var records = await Adapter(handler).FetchAsync(Store, CancellationToken.None);

        Assert.Equal(4, records.Count);
        Assert.Equal(3, handler.PageRequests);
    }

    [Fact]
    public async Task Fetch_handles_windowed_pagination()
    {
        // Each page links only to its neighbours, like "‹ 1 2 3 ›".
        var handler = new FakeHandler(uri =>
        {
            var p = PageNumber(uri);
            return p <= 5
                ? Html([$"P{p}a", $"P{p}b"], linkedPages: p == 5 ? new[] { 4, 5 } : new[] { p, p + 1 })
                : throw new InvalidOperationException("past last page");
        });

        var records = await Adapter(handler).FetchAsync(Store, CancellationToken.None);

        Assert.Equal(10, records.Count);
    }

    [Fact]
    public async Task Fetch_drops_exact_duplicates_and_keeps_first_of_conflicting()
    {
        var handler = new FakeHandler(_ => Html(["МЛЕКО", "МЛЕКО"], prices: [60, 65]));

        var records = await Adapter(handler).FetchAsync(Store, CancellationToken.None);

        var only = Assert.Single(records);
        Assert.Equal(60, only.Price);
    }

    [Fact]
    public async Task Fetch_fails_when_page_one_has_no_rows()
    {
        var handler = new FakeHandler(_ => "<html><body>maintenance</body></html>");
        await Assert.ThrowsAsync<MisaFormatException>(() => Adapter(handler).FetchAsync(Store, CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_fails_instead_of_truncating_at_max_pages()
    {
        var handler = new FakeHandler(uri => Html([$"X{PageNumber(uri)}", $"Y{PageNumber(uri)}"],
            linkedPages: [PageNumber(uri) + 1]));
        var adapter = Adapter(handler, o => o.MaxPages = 4);

        await Assert.ThrowsAsync<MisaFormatException>(() => adapter.FetchAsync(Store, CancellationToken.None));
    }

    [Fact]
    public async Task Probe_prefers_etag()
    {
        var handler = new FakeHandler(_ => Html(["А"]), etag: "\"v42\"");
        var fp = await Adapter(handler).ProbeAsync(Store, CancellationToken.None);
        Assert.Equal("etag:\"v42\"", fp.Value);
    }

    [Fact]
    public async Task Probe_hash_is_stable_and_changes_with_prices()
    {
        var a = await Adapter(new FakeHandler(_ => Html(["А"], prices: [10]))).ProbeAsync(Store, CancellationToken.None);
        var b = await Adapter(new FakeHandler(_ => Html(["А"], prices: [10]))).ProbeAsync(Store, CancellationToken.None);
        var c = await Adapter(new FakeHandler(_ => Html(["А"], prices: [11]))).ProbeAsync(Store, CancellationToken.None);

        Assert.StartsWith("sha256:", a.Value);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task Respects_robots_disallow()
    {
        var handler = new FakeHandler(_ => Html(["А"]), robots: "User-agent: *\nDisallow: /");
        await Assert.ThrowsAsync<CrawlDisallowedException>(() =>
            Adapter(handler).ProbeAsync(Store, CancellationToken.None));
        Assert.Equal(0, handler.PageRequests);
    }

    [Fact]
    public async Task Retries_transient_failures()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => Html(["А"]))
        {
            Override = () => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)) },
            } : null,
        };

        var fp = await Adapter(handler).ProbeAsync(Store, CancellationToken.None);
        Assert.StartsWith("sha256:", fp.Value);
    }

    [Fact]
    public async Task Discover_returns_the_nine_documented_orgs()
    {
        var stores = await Adapter(new FakeHandler(_ => "")).DiscoverStoresAsync(CancellationToken.None);
        Assert.Equal(new[] { "2", "3", "4", "7", "8", "9", "11", "12", "13" }, stores.Select(s => s.ExternalId));
    }

    // ---- helpers ----

    private static MisaAdapter Adapter(FakeHandler handler, Action<MisaOptions>? configure = null)
    {
        var misa = new MisaOptions { PageDelay = TimeSpan.Zero, PageDelayJitter = TimeSpan.Zero, PerPage = 2 };
        configure?.Invoke(misa);
        return new MisaAdapter(TestHttp.Polite(handler), Options.Create(misa), NullLogger<MisaAdapter>.Instance);
    }

    private static int PageNumber(Uri uri)
    {
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return int.Parse(q["page"]!);
    }

    private static string Html(string[] names, int[]? linkedPages = null, int[]? prices = null)
    {
        var sb = new StringBuilder("<table><thead><tr><th>Назив на стока-производ</th><th>Продажна цена</th>"
                                   + "<th>Единечна цена</th><th>Опис на стока</th><th>Достапност во продажен објект</th>"
                                   + "<th>Редовна цена</th></tr></thead><tbody>");
        for (var i = 0; i < names.Length; i++)
        {
            var price = prices?[i] ?? 100 + i;
            sb.Append($"<tr><td>{names[i]}</td><td>{price} ден.</td><td></td><td>X</td><td>Да</td><td>{price} ден.</td></tr>");
        }
        sb.Append("</tbody></table>");
        foreach (var p in linkedPages ?? Array.Empty<int>())
        {
            sb.Append($"<a href=\"?org=3&search=&perPage=2&page={p}\">{p}</a>");
        }
        return sb.ToString();
    }

    private sealed class FakeHandler(Func<Uri, string> page, string? etag = null, string? robots = null)
        : HttpMessageHandler
    {
        public int PageRequests { get; private set; }
        public Func<HttpResponseMessage?>? Override { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/robots.txt")
            {
                return Task.FromResult(robots is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(robots) });
            }

            PageRequests++;
            if (Override?.Invoke() is { } overridden)
            {
                return Task.FromResult(overridden);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page(uri), Encoding.UTF8, "text/html"),
            };
            if (etag is not null)
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            }
            return Task.FromResult(response);
        }
    }

}

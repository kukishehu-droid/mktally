using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Kam;

namespace Tally.Infrastructure.Tests.Ingestion;

public class KamAdapterTests
{
    private static StoreDescriptor Store(string id) => new() { ChainCode = "kam", ExternalId = id, Name = id };

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Kam", name);

    [Fact]
    public async Task Discover_filters_on_city_and_carries_the_published_details()
    {
        var stores = await Adapter(new FakeKam()).DiscoverStoresAsync(CancellationToken.None);

        Assert.Equal(new[] { "38", "64", "69" }, stores.Select(s => s.ExternalId));
        var genc = stores[0];
        Assert.Equal("kam", genc.ChainCode);
        Assert.Equal("Генц", genc.Name);
        Assert.Equal("ул. Попово Блато бр.1/приземје - 1", genc.Address);
        Assert.Equal("Струга", genc.City);
        Assert.Equal(41.18688301123239, genc.Latitude);
        Assert.Equal("08:30-21:00", genc.WorkingHours);
        Assert.Equal("09:00-21:00", genc.SundayWorkingHours);
        Assert.Equal("071/291-388", genc.Phone);
    }

    [Fact]
    public async Task Discover_honours_configured_cities()
    {
        var stores = await Adapter(new FakeKam(), o => o.Cities = ["Радовиш"]).DiscoverStoresAsync(CancellationToken.None);
        Assert.Equal("73", Assert.Single(stores).ExternalId);
    }

    [Fact]
    public async Task Discover_fails_when_no_store_is_in_scope()
    {
        await Assert.ThrowsAsync<KamFormatException>(() =>
            Adapter(new FakeKam(), o => o.Cities = ["Охрид"]).DiscoverStoresAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Probing_every_store_costs_one_request()
    {
        var server = new FakeKam();
        var adapter = Adapter(server);

        var fps = new List<SourceFingerprint>();
        foreach (var id in new[] { "38", "64", "69" })
        {
            fps.Add(await adapter.ProbeAsync(Store(id), CancellationToken.None));
        }

        Assert.Equal(1, server.ShopListRequests);
        Assert.Equal("file:2026/11/22/38.pdf|uploaded:1795302000000", fps[0].Value);
        Assert.Null(fps[0].PublishedOn); // the upload date is not the price date
        Assert.Equal(3, fps.Select(f => f.Value).Distinct().Count());
    }

    [Fact]
    public async Task Store_list_is_refetched_after_the_cache_expires()
    {
        var server = new FakeKam();
        var time = new ManualTime(new DateTimeOffset(2026, 11, 23, 3, 20, 0, TimeSpan.Zero));
        var adapter = Adapter(server, time: time);

        await adapter.ProbeAsync(Store("38"), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(31));
        await adapter.ProbeAsync(Store("38"), CancellationToken.None);

        Assert.Equal(2, server.ShopListRequests);
    }

    [Fact]
    public async Task Fetch_reads_the_pdf_and_dates_records_by_the_stamp()
    {
        var server = new FakeKam();
        var records = await Adapter(server).FetchAsync(Store("38"), CancellationToken.None);

        Assert.Equal(33, records.Count);
        Assert.All(records, r => Assert.Equal(new DateOnly(2026, 11, 18), r.PublishedOn)); // URL says 22.11
        Assert.Equal(new[] { "/2026/11/22/38.pdf" }, server.PdfRequests);
        Assert.Contains(records, r => r is { NamePrimary: "ДУО КЕКС ГРИСОН 500ГР", PromoPrice: 102 });
    }

    [Fact]
    public async Task Empty_body_where_the_pdf_should_be_is_no_price_list()
    {
        // Live: missing dated paths answer 200 with no content.
        var ex = await Assert.ThrowsAsync<KamNoPriceListException>(() =>
            Adapter(new FakeKam()).FetchAsync(Store("69"), CancellationToken.None));
        Assert.Contains("empty body", ex.Message);
    }

    [Fact]
    public async Task Non_pdf_body_is_no_price_list()
    {
        var server = new FakeKam { Pdfs = { ["/2026/11/22/64.pdf"] = Encoding.UTF8.GetBytes("<html>Грешка</html>") } };
        await Assert.ThrowsAsync<KamNoPriceListException>(() =>
            Adapter(server).FetchAsync(Store("64"), CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_body_is_refused()
    {
        await Assert.ThrowsAsync<KamFormatException>(() =>
            Adapter(new FakeKam(), o => o.MaxPdfBytes = 1000).FetchAsync(Store("38"), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_store_fails_clearly()
    {
        var ex = await Assert.ThrowsAsync<KamFormatException>(() =>
            Adapter(new FakeKam()).FetchAsync(Store("999"), CancellationToken.None));
        Assert.Contains("999", ex.Message);
    }

    [Theory]
    [InlineData("2026/11/22/38.pdf", "https://kam.com.mk/2026/11/22/38.pdf")]
    [InlineData("/2026/11/22/38.pdf", "https://kam.com.mk/2026/11/22/38.pdf")]
    public void Price_list_paths_resolve_on_the_kam_site(string path, string expected) =>
        Assert.Equal(new Uri(expected), Adapter(new FakeKam()).PriceListUri(path));

    [Theory]
    [InlineData("../secret.pdf")]
    [InlineData("https://elsewhere.example/38.pdf")]
    [InlineData("//elsewhere.example/38.pdf")]
    [InlineData("2026/11/22/38.exe")]
    public void Suspicious_price_list_paths_are_refused(string path) =>
        Assert.Throws<KamFormatException>(() => Adapter(new FakeKam()).PriceListUri(path));

    // ---- helpers ----

    private static KamAdapter Adapter(FakeKam server, Action<KamOptions>? configure = null, TimeProvider? time = null)
    {
        var options = new KamOptions { RequestDelay = TimeSpan.Zero, RequestDelayJitter = TimeSpan.Zero };
        configure?.Invoke(options);
        return new KamAdapter(TestHttp.Polite(server), Options.Create(options), time ?? TimeProvider.System,
            NullLogger<KamAdapter>.Instance);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// kam.com.mk as seen on 2026-11-22: empty robots.txt (200), the store list on POST, the PDF
    /// on GET, and 200 with an empty body for any other path.
    /// </summary>
    private sealed class FakeKam : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Pdfs { get; } = new()
        {
            ["/2026/11/22/38.pdf"] = File.ReadAllBytes(FixturePath("price_38_trimmed.pdf")),
        };

        public int ShopListRequests { get; private set; }
        public List<string> PdfRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/robots.txt")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
            }

            if (path == "/ShopsWeb/LoadShopList" && request.Method == HttpMethod.Post)
            {
                ShopListRequests++;
                var json = File.ReadAllText(FixturePath("shop_list_sample.json"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            if (path.EndsWith(".pdf", StringComparison.Ordinal))
            {
                PdfRequests.Add(path);
            }

            if (Pdfs.TryGetValue(path, out var bytes))
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        }
    }
}

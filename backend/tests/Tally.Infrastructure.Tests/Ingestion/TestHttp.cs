using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tally.Infrastructure.Ingestion.Http;

namespace Tally.Infrastructure.Tests.Ingestion;

internal static class TestHttp
{
    /// <summary>A <see cref="PoliteHttp"/> over a fake handler, with real robots.txt handling.</summary>
    public static PoliteHttp Polite(HttpMessageHandler handler)
    {
        var factory = new FakeFactory(handler);
        var crawler = Options.Create(new CrawlerOptions { ContactUrl = "https://example.test/bot" });
        var robots = new RobotsTxtPolicy(factory, crawler, TimeProvider.System, NullLogger<RobotsTxtPolicy>.Instance);
        return new PoliteHttp(factory, robots, crawler, TimeProvider.System, NullLogger<PoliteHttp>.Instance);
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

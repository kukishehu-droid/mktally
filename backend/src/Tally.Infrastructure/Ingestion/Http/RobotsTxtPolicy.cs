using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tally.Infrastructure.Ingestion.Http;

/// <summary>
/// Fetches and caches robots.txt per host. RFC 9309 failure handling: 4xx means no
/// restrictions; 5xx or unreachable means assume everything is disallowed.
/// </summary>
public sealed class RobotsTxtPolicy
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpFactory;
    private readonly CrawlerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RobotsTxtPolicy> _logger;
    private readonly ConcurrentDictionary<string, (RobotsTxt Rules, DateTimeOffset FetchedAt)> _cache = new();

    public RobotsTxtPolicy(
        IHttpClientFactory httpFactory,
        IOptions<CrawlerOptions> options,
        TimeProvider time,
        ILogger<RobotsTxtPolicy> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<bool> IsAllowedAsync(Uri uri, CancellationToken ct)
    {
        var rules = await GetRulesAsync(uri, ct);
        return rules.IsAllowed(uri.PathAndQuery);
    }

    /// <exception cref="CrawlDisallowedException">robots.txt disallows <paramref name="uri"/>.</exception>
    public async Task EnsureAllowedAsync(Uri uri, CancellationToken ct)
    {
        if (!await IsAllowedAsync(uri, ct))
        {
            throw new CrawlDisallowedException(uri);
        }
    }

    private async Task<RobotsTxt> GetRulesAsync(Uri uri, CancellationToken ct)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (_cache.TryGetValue(origin, out var cached) && _time.GetUtcNow() - cached.FetchedAt < CacheFor)
        {
            return cached.Rules;
        }

        var rules = await FetchAsync(new Uri(origin + "/robots.txt"), ct);
        _cache[origin] = (rules, _time.GetUtcNow());
        return rules;
    }

    private async Task<RobotsTxt> FetchAsync(Uri robotsUri, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(CrawlerHttp.ClientName);
            using var response = await http.GetAsync(robotsUri, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return RobotsTxt.Parse(body, _options.ProductToken);
            }

            var status = (int)response.StatusCode;
            if (status is >= 400 and < 500)
            {
                _logger.LogInformation("{Uri} returned {Status}; treating as no restrictions", robotsUri, status);
                return RobotsTxt.AllowAll;
            }

            _logger.LogWarning("{Uri} returned {Status}; treating as fully disallowed", robotsUri, status);
            return RobotsTxt.DisallowAll;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Uri} unreachable; treating as fully disallowed", robotsUri);
            return RobotsTxt.DisallowAll;
        }
    }
}

public sealed class CrawlDisallowedException(Uri uri)
    : Exception($"robots.txt disallows {uri}")
{
    public Uri Uri { get; } = uri;
}

public static class CrawlerHttp
{
    /// <summary>Named HttpClient carrying the crawler User-Agent. Registered in <c>AddTallyCrawlers</c>.</summary>
    public const string ClientName = "tally-crawler";

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;
}

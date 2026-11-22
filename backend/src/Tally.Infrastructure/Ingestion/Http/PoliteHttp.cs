using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tally.Infrastructure.Ingestion.Http;

/// <summary>
/// The only way adapters talk to chain websites. Every request is checked against
/// robots.txt, carries the crawler User-Agent (named client), and is retried a few times
/// on transient failures. A Cloudflare challenge is reported, never worked around.
/// </summary>
public sealed class PoliteHttp
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly RobotsTxtPolicy _robots;
    private readonly CrawlerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PoliteHttp> _logger;

    public PoliteHttp(
        IHttpClientFactory httpFactory,
        RobotsTxtPolicy robots,
        IOptions<CrawlerOptions> options,
        TimeProvider time,
        ILogger<PoliteHttp> logger)
    {
        _httpFactory = httpFactory;
        _robots = robots;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, uri, null, ct);

    public Task<HttpResponseMessage> PostFormAsync(
        Uri uri, IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, uri, () => new FormUrlEncodedContent(form), ct);

    public async Task<string> GetStringAsync(Uri uri, CancellationToken ct)
    {
        using var response = await GetAsync(uri, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Waits between consecutive requests to the same site.</summary>
    public Task PauseAsync(TimeSpan delay, TimeSpan jitter, CancellationToken ct) =>
        Task.Delay(delay + jitter * Random.Shared.NextDouble(), _time, ct);

    /// <summary>Returns only successful responses; the caller disposes.</summary>
    /// <exception cref="CrawlDisallowedException">robots.txt disallows the URL.</exception>
    /// <exception cref="CrawlBlockedException">The site served a bot challenge.</exception>
    /// <exception cref="HttpRequestException">Non-success after retries.</exception>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri uri, Func<HttpContent>? content, CancellationToken ct)
    {
        await _robots.EnsureAllowedAsync(uri, ct);
        var http = _httpFactory.CreateClient(CrawlerHttp.ClientName);
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                var request = new HttpRequestMessage(method, uri) { Content = content?.Invoke() };
                response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (IsBotChallenge(response))
                {
                    response.Dispose();
                    throw new CrawlBlockedException(uri);
                }

                if (!CrawlerHttp.IsTransient(response.StatusCode) || attempt >= maxAttempts)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"{method} {uri} returned {(int)status}", null, status);
                }

                var wait = RetryDelay(response, attempt);
                _logger.LogWarning("{Method} {Uri} returned {Status}; retry {Attempt} in {Wait}",
                    method, uri, (int)response.StatusCode, attempt, wait);
                response.Dispose();
                await Task.Delay(wait, _time, ct);
            }
            catch (Exception ex) when (
                ex is HttpRequestException { StatusCode: null } or TaskCanceledException
                && !ct.IsCancellationRequested
                && attempt < maxAttempts)
            {
                response?.Dispose();
                var wait = BackoffFor(attempt);
                _logger.LogWarning(ex, "{Method} {Uri} failed; retry {Attempt} in {Wait}", method, uri, attempt, wait);
                await Task.Delay(wait, _time, ct);
            }
        }
    }

    // Cloudflare marks challenges explicitly; a 403 from Cloudflare itself is a block too.
    private static bool IsBotChallenge(HttpResponseMessage response)
    {
        if (response.Headers.Contains("cf-mitigated"))
        {
            return true;
        }
        var server = response.Headers.Server.ToString();
        return response.StatusCode == System.Net.HttpStatusCode.Forbidden
               && server.Contains("cloudflare", StringComparison.OrdinalIgnoreCase);
    }

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var hinted = retryAfter?.Delta
                     ?? (retryAfter?.Date is { } date ? date - _time.GetUtcNow() : (TimeSpan?)null);
        if (hinted is { } h && h > TimeSpan.Zero)
        {
            return h < TimeSpan.FromMinutes(2) ? h : TimeSpan.FromMinutes(2);
        }
        return BackoffFor(attempt);
    }

    private static TimeSpan BackoffFor(int attempt) => TimeSpan.FromSeconds(2 * Math.Pow(4, attempt - 1));
}

/// <summary>
/// The site answered with a bot challenge. Do not try to get around it: ask the chain to
/// allow the crawler, and use the manual import (REQUIREMENTS.md §5.5) meanwhile.
/// </summary>
public sealed class CrawlBlockedException(Uri uri)
    : Exception($"{uri.Host} served a bot challenge to {uri}. Not retrying; see REQUIREMENTS.md §5.5 for the manual import fallback.")
{
    public Uri Uri { get; } = uri;
}

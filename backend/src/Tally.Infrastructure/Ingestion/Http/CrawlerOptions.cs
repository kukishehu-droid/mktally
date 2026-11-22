namespace Tally.Infrastructure.Ingestion.Http;

/// <summary>
/// Shared crawler identity. CLAUDE.md: honest User-Agent with a contact URL.
/// </summary>
public sealed class CrawlerOptions
{
    public const string SectionName = "Crawlers";

    /// <summary>Product token used in the User-Agent and matched against robots.txt groups.</summary>
    public string ProductToken { get; set; } = "TallyBot";

    public string Version { get; set; } = "0.1";

    /// <summary>Required. A page explaining what the crawler is and how to reach the operator.</summary>
    public string ContactUrl { get; set; } = "";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Attempts per request, including the first. Retries only transient failures.</summary>
    public int MaxAttempts { get; set; } = 3;

    public string UserAgent => $"{ProductToken}/{Version} (+{ContactUrl})";
}

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Http;
using Tally.Infrastructure.Ingestion.Kipper;
using Tally.Infrastructure.Ingestion.Misa;

namespace Tally.Infrastructure.Ingestion;

public static class IngestionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the crawler HttpClient, robots.txt policy and chain adapters.
    /// Worker host only: the API host must not carry crawler dependencies (backend/CLAUDE.md).
    /// Binds <c>Crawlers</c> and <c>Crawlers:Misa</c> from configuration.
    /// </summary>
    public static IServiceCollection AddTallyCrawlers(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<CrawlerOptions>()
            .BindConfiguration(CrawlerOptions.SectionName)
            .Validate(o => Uri.TryCreate(o.ContactUrl, UriKind.Absolute, out _),
                "Crawlers:ContactUrl must be an absolute URL. The crawler identifies itself honestly.")
            .ValidateOnStart();

        services.AddOptions<MisaOptions>()
            .BindConfiguration(MisaOptions.SectionName)
            .Validate(o => o.PerPage is > 0 and <= 100, "Crawlers:Misa:PerPage must be 1-100")
            .Validate(o => o.MaxPages > 0, "Crawlers:Misa:MaxPages must be positive");

        services.AddHttpClient(CrawlerHttp.ClientName, (sp, client) =>
            {
                var o = sp.GetRequiredService<IOptions<CrawlerOptions>>().Value;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(o.UserAgent);
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("mk");
                client.Timeout = o.RequestTimeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });

        services.AddOptions<KipperOptions>()
            .BindConfiguration(KipperOptions.SectionName)
            .Validate(o => o.PageSize > 0, "Crawlers:Kipper:PageSize must be positive")
            .Validate(o => o.MaxPages > 0, "Crawlers:Kipper:MaxPages must be positive");

        services.AddSingleton<RobotsTxtPolicy>();
        services.AddSingleton<PoliteHttp>();

        services.AddSingleton<MisaAdapter>();
        services.AddSingleton<IPriceSourceAdapter>(sp => sp.GetRequiredService<MisaAdapter>());
        services.AddSingleton<KipperAdapter>();
        services.AddSingleton<IPriceSourceAdapter>(sp => sp.GetRequiredService<KipperAdapter>());

        return services;
    }
}

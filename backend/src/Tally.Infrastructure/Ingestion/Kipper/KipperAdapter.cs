using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Http;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Kipper;

/// <summary>
/// Kipper via its WordPress DataTables endpoint. REQUIREMENTS.md §4.1.
/// </summary>
/// <remarks>
/// <para>Probe: GET the store page (one request), fingerprint = its "last price update"
/// timestamp, which also becomes <c>published_on</c>.</para>
/// <para>Fetch: GET the store page again (for its post id and the timestamp), then POST
/// <c>admin-ajax.php</c> in pages of <see cref="KipperOptions.PageSize"/> until
/// <c>recordsFiltered</c> is reached. About six requests per store.</para>
/// <para>On 2026-11-22 all three Struga stores showed the same timestamp
/// (20 November 15:43), so it looks like a chain-wide import time: Kipper's prices lag
/// about two days, like KAM's.</para>
/// </remarks>
public sealed class KipperAdapter : IPriceSourceAdapter
{
    public const string Code = "kipper";

    private readonly PoliteHttp _http;
    private readonly KipperOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<KipperAdapter> _logger;

    public KipperAdapter(PoliteHttp http, IOptions<KipperOptions> options, TimeProvider time, ILogger<KipperAdapter> logger)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public string ChainCode => Code;

    private IReadOnlyList<KipperStoreOptions> Roster =>
        _options.Stores.Count > 0 ? _options.Stores : KipperStoreRoster.Struga;

    /// <summary>
    /// Roster plus coordinates read from each store page. A page that fails to load still
    /// yields the store, without coordinates, so one bad page does not hide a store.
    /// </summary>
    public async Task<IReadOnlyList<StoreDescriptor>> DiscoverStoresAsync(CancellationToken ct)
    {
        var result = new List<StoreDescriptor>();
        var first = true;
        foreach (var s in Roster)
        {
            if (!first)
            {
                await _http.PauseAsync(_options.PageDelay, _options.PageDelayJitter, ct);
            }
            first = false;

            KipperStorePage? page = null;
            try
            {
                page = KipperStorePageParser.Parse(await _http.GetStringAsync(StorePageUri(s), ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or CrawlBlockedException)
            {
                _logger.LogWarning(ex, "Kipper store page for {StoreId} unavailable during discovery", s.StoreId);
            }

            result.Add(new StoreDescriptor
            {
                ChainCode = Code,
                ExternalId = s.StoreId,
                Name = s.Name,
                City = s.City,
                Latitude = page?.Latitude,
                Longitude = page?.Longitude,
            });
        }
        return result;
    }

    public async Task<SourceFingerprint> ProbeAsync(StoreDescriptor store, CancellationToken ct)
    {
        var page = KipperStorePageParser.Parse(await _http.GetStringAsync(StorePageUri(RosterEntry(store)), ct));

        if (page.LastPriceUpdate is { } updated)
        {
            return new SourceFingerprint(
                "updated:" + updated.ToString("O", CultureInfo.InvariantCulture),
                LocalDate(updated));
        }

        // No timestamp means the markup changed. Degrade to a fetch every night (about six
        // requests) instead of going dark, and say so loudly.
        _logger.LogWarning(
            "Kipper store {StoreId}: no 'last price update' timestamp on the store page; forcing a full fetch",
            store.ExternalId);
        return new SourceFingerprint("unknown:" + _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<RawPriceRecord>> FetchAsync(StoreDescriptor store, CancellationToken ct)
    {
        var entry = RosterEntry(store);
        var storePage = KipperStorePageParser.Parse(await _http.GetStringAsync(StorePageUri(entry), ct));
        var publishedOn = storePage.LastPriceUpdate is { } u ? LocalDate(u) : (DateOnly?)null;

        var postId = entry.PostId ?? storePage.PostId
            ?? throw new KipperFormatException(
                $"Kipper {entry.StoreId}: no postid-NNNNN on the store page. Set Crawlers:Kipper:Stores[].PostId.");

        var ajaxUri = new Uri(_options.BaseUrl, _options.AjaxPath);
        var records = new List<RawPriceRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? total = null;
        var start = 0;
        var skipped = 0;
        var duplicates = 0;
        var completed = false;

        for (var draw = 1; draw <= _options.MaxPages; draw++)
        {
            await _http.PauseAsync(_options.PageDelay, _options.PageDelayJitter, ct);

            var form = BuildForm(postId, draw, start);
            string body;
            using (var response = await _http.PostFormAsync(ajaxUri, form, ct))
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }

            var page = KipperDataPage.Parse(body);
            if (page.Draw is { } echoed && echoed != draw)
            {
                throw new KipperFormatException($"DataTables draw mismatch: sent {draw}, got {echoed}.");
            }

            total ??= page.RecordsFiltered ?? page.RecordsTotal;

            foreach (var row in page.Rows)
            {
                if (!KipperRecordMapper.TryMap(row, publishedOn, out var record, out var reason))
                {
                    skipped++;
                    _logger.LogDebug("Kipper {StoreId}: skipped row ({Reason})", entry.StoreId, reason);
                    continue;
                }
                if (!seen.Add(record.SourceKey))
                {
                    duplicates++;
                    continue;
                }
                records.Add(record);
            }

            // Advance by what actually came back, so a server-side cap below PageSize works.
            start += page.Rows.Count;
            if (page.Rows.Count == 0 || (total is { } t && start >= t))
            {
                completed = true;
                break;
            }
        }

        if (!completed)
        {
            throw new KipperFormatException(
                $"Kipper {entry.StoreId}: not finished after {_options.MaxPages} requests ({start} of {total} rows).");
        }

        if (start == 0)
        {
            throw new KipperFormatException(
                $"Kipper {entry.StoreId}: the price table came back empty. The store parameter may be wrong (Crawlers:Kipper:StoreParamName).");
        }

        if (total is { } expected && start < expected)
        {
            _logger.LogWarning("Kipper {StoreId}: server reported {Expected} rows but returned {Got}",
                entry.StoreId, expected, start);
        }

        _logger.LogInformation(
            "Kipper {StoreId}: {Count} records ({Skipped} skipped, {Duplicates} duplicate keys), published {PublishedOn}",
            entry.StoreId, records.Count, skipped, duplicates, publishedOn);

        return records;
    }

    private List<KeyValuePair<string, string>> BuildForm(string postId, int draw, int start)
    {
        // The minimal set the endpoint accepted on 2026-11-22. Rows came back ordered by
        // name without an explicit order parameter; paging relies on that being stable,
        // and duplicate keys or a short total are logged if it is not.
        var form = new List<KeyValuePair<string, string>>
        {
            new("action", _options.AjaxAction),
            new(_options.StoreParamName, postId),
            new("draw", draw.ToString(CultureInfo.InvariantCulture)),
            new("start", start.ToString(CultureInfo.InvariantCulture)),
            new("length", _options.PageSize.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var (key, value) in _options.ExtraFormFields)
        {
            form.Add(new(key, value));
        }
        return form;
    }

    private KipperStoreOptions RosterEntry(StoreDescriptor store) =>
        Roster.FirstOrDefault(s => s.StoreId == store.ExternalId)
        ?? throw new InvalidOperationException(
            $"Kipper store {store.ExternalId} is not in the roster (Crawlers:Kipper:Stores), so its page path is unknown.");

    private Uri StorePageUri(KipperStoreOptions store) => new(_options.BaseUrl, store.PagePath);

    private static DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, SkopjeTime.Zone).DateTime);
}

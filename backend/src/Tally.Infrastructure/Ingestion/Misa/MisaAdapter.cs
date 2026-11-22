using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Http;

namespace Tally.Infrastructure.Ingestion.Misa;

/// <summary>
/// Misa via proverkanaceni.mk. Plain GET, HTML table, <c>?org={id}&amp;perPage=&amp;page=</c>.
/// REQUIREMENTS.md §4.3.
/// </summary>
/// <remarks>
/// Probe: one request (page 1). Fingerprint from ETag, else Last-Modified, else a hash of the
/// parsed rows on page 1 plus the page count. Verified 2026-11-22: the server (IIS) sends
/// neither header, so the hash is what runs. A change confined to a later page with page 1
/// and the page count unchanged is not seen until the weekly forced re-parse.
/// The page's "last updated" stamp cannot help: it shows the current time.
/// </remarks>
public sealed class MisaAdapter : IPriceSourceAdapter
{
    public const string Code = "misa";

    private readonly PoliteHttp _http;
    private readonly MisaOptions _options;
    private readonly ILogger<MisaAdapter> _logger;

    public MisaAdapter(PoliteHttp http, IOptions<MisaOptions> options, ILogger<MisaAdapter> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public string ChainCode => Code;

    public Task<IReadOnlyList<StoreDescriptor>> DiscoverStoresAsync(CancellationToken ct)
    {
        var roster = _options.Stores.Count > 0 ? _options.Stores : MisaStoreRoster.All;
        IReadOnlyList<StoreDescriptor> stores = roster
            .Select(s => new StoreDescriptor
            {
                ChainCode = Code,
                ExternalId = s.Org,
                Name = s.Name,
                City = s.City,
                Municipality = s.Municipality,
            })
            .ToList();
        return Task.FromResult(stores);
    }

    public async Task<SourceFingerprint> ProbeAsync(StoreDescriptor store, CancellationToken ct)
    {
        using var response = await _http.GetAsync(PageUri(store.ExternalId, 1), ct);

        if (response.Headers.ETag is { } etag)
        {
            return new SourceFingerprint("etag:" + etag.Tag);
        }

        if (response.Content.Headers.LastModified is { } lastModified)
        {
            return new SourceFingerprint(
                "last-modified:" + lastModified.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        var page = MisaPageParser.Parse(await response.Content.ReadAsStringAsync(ct));
        if (page.Rows.Count == 0)
        {
            throw new MisaFormatException(
                $"No price rows on page 1 for org={store.ExternalId}. The page layout may have changed.");
        }

        return new SourceFingerprint("sha256:" + page.ContentHash);
    }

    public async Task<IReadOnlyList<RawPriceRecord>> FetchAsync(StoreDescriptor store, CancellationToken ct)
    {
        var org = store.ExternalId;
        var records = new List<RawPriceRecord>();
        var seen = new Dictionary<string, RawPriceRecord>(StringComparer.Ordinal);
        var skipped = 0;
        var conflicting = 0;

        var pageSize = 0;
        var rowsSeen = 0;
        int? reportedTotal = null;
        string? previousHash = null;
        var completed = false;

        for (var pageNo = 1; pageNo <= _options.MaxPages; pageNo++)
        {
            if (pageNo > 1)
            {
                await _http.PauseAsync(_options.PageDelay, _options.PageDelayJitter, ct);
            }

            var page = MisaPageParser.Parse(await _http.GetStringAsync(PageUri(org, pageNo), ct));

            if (pageNo == 1)
            {
                if (page.Rows.Count == 0)
                {
                    throw new MisaFormatException(
                        $"No price rows on page 1 for org={org}. The page layout may have changed.");
                }
                // The server may ignore perPage; learn the real page size from page 1.
                pageSize = page.Rows.Count;
                reportedTotal = page.TotalItems;
            }

            // Past the end: either an empty page, or the site clamps to the last page and
            // returns the same rows again.
            if (page.Rows.Count == 0 || page.ContentHash == previousHash)
            {
                completed = true;
                break;
            }
            previousHash = page.ContentHash;
            rowsSeen += page.Rows.Count;

            foreach (var row in page.Rows)
            {
                if (!MisaRecordMapper.TryMap(row, out var record, out var reason))
                {
                    skipped++;
                    _logger.LogDebug("Misa org={Org} page {Page}: skipped row ({Reason})", org, pageNo, reason);
                    continue;
                }

                if (seen.TryGetValue(record.SourceKey, out var existing))
                {
                    // Same name listed twice. Identical rows are harmless; differing ones mean
                    // two products share a display name, and one price would be wrong either
                    // way, so keep the first and count it for review.
                    if (existing != record)
                    {
                        conflicting++;
                        _logger.LogWarning(
                            "Misa org={Org}: '{Name}' listed twice with different data ({A} vs {B} den); kept first",
                            org, record.NamePrimary, existing.Price, record.Price);
                    }
                    continue;
                }

                seen.Add(record.SourceKey, record);
                records.Add(record);
            }

            // Per-page check, so a windowed paginator ("1 2 3 … next") still works: the last
            // page is the one that links to no higher page. No pagination links at all → a
            // short page is the last one.
            if (page.LastPage is { } lp ? pageNo >= lp : page.Rows.Count < pageSize)
            {
                completed = true;
                break;
            }
        }

        if (!completed)
        {
            throw new MisaFormatException(
                $"org={org}: still finding rows after {_options.MaxPages} pages. Raise MaxPages if the catalogue really grew.");
        }

        if (reportedTotal is { } expected && rowsSeen != expected)
        {
            _logger.LogWarning("Misa org={Org}: page reports {Expected} items but {Seen} rows were read",
                org, expected, rowsSeen);
        }

        _logger.LogInformation(
            "Misa org={Org}: {Count} records, {Skipped} rows skipped, {Conflicting} conflicting duplicates",
            org, records.Count, skipped, conflicting);

        return records;
    }

    private Uri PageUri(string org, int page) =>
        new(_options.BaseUrl,
            $"?org={Uri.EscapeDataString(org)}&search=&perPage={_options.PerPage}&page={page}");
}

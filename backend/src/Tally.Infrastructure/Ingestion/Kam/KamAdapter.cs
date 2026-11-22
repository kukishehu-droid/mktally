using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tally.Application.Ingestion;
using Tally.Infrastructure.Ingestion.Http;
using Tally.Infrastructure.Ingestion.Parsing;

namespace Tally.Infrastructure.Ingestion.Kam;

/// <summary>
/// KAM via its store-list JSON and per-store PDF price lists. REQUIREMENTS.md §4.2.
/// </summary>
/// <remarks>
/// <para>Discover and probe: <c>POST ShopsWeb/LoadShopList</c>, cached for
/// <see cref="KamOptions.ShopListCacheFor"/>, so probing every store in a run costs one
/// request. Fingerprint = the current file's <c>RelativePath</c> + <c>DateUploaded</c>
/// (REQUIREMENTS.md §5.2).</para>
/// <para>Verified 2026-11-22: KAM re-posts every store's file under each day's path
/// (<c>2026/11/20/73.pdf</c> and <c>2026/11/22/73.pdf</c> were byte-identical), so the
/// fingerprint changes nightly even when prices do not, and each store's ~0.5 MB PDF is
/// fetched nightly. Real change shows in <see cref="RawPriceRecord.PublishedOn"/>, which is
/// the PDF's own snapshot stamp, not the URL date.</para>
/// <para>Fetch: one GET for the PDF. A missing path answers HTTP 200 with an empty body,
/// not 404.</para>
/// </remarks>
public sealed class KamAdapter : IPriceSourceAdapter
{
    public const string Code = "kam";

    private readonly PoliteHttp _http;
    private readonly KamOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<KamAdapter> _logger;
    private readonly SemaphoreSlim _shopListLock = new(1, 1);
    private (IReadOnlyList<KamShop> Shops, DateTimeOffset LoadedAt)? _shopList;

    public KamAdapter(PoliteHttp http, IOptions<KamOptions> options, TimeProvider time, ILogger<KamAdapter> logger)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public string ChainCode => Code;

    private IReadOnlyList<string> Cities => _options.Cities.Count > 0 ? _options.Cities : KamOptions.DefaultCities;

    private KamTableLayout Layout => _options.ColumnEdges.Count > 0
        ? KamTableLayout.Default with { ColumnEdges = _options.ColumnEdges }
        : KamTableLayout.Default;

    /// <summary>Every store whose <c>City</c> is in scope, with the details KAM publishes.</summary>
    public async Task<IReadOnlyList<StoreDescriptor>> DiscoverStoresAsync(CancellationToken ct)
    {
        var shops = await GetShopsAsync(ct);
        var inScope = shops.Where(InScope).ToList();
        if (inScope.Count == 0)
        {
            throw new KamFormatException(
                $"No KAM store has City in [{string.Join(", ", Cities)}] among {shops.Count} stores. " +
                "The City field may have changed; check Crawlers:Kam:Cities.");
        }

        return inScope
            .Select(s => new StoreDescriptor
            {
                ChainCode = Code,
                ExternalId = s.Id.ToString(CultureInfo.InvariantCulture),
                Name = s.Name,
                Address = s.Address,
                City = s.City,
                Municipality = s.Municipality,
                Latitude = s.Latitude,
                Longitude = s.Longitude,
                WorkingHours = s.WorkingHours,
                SundayWorkingHours = s.SundayWorkingHours,
                Phone = s.Phone,
            })
            .ToList();
    }

    public async Task<SourceFingerprint> ProbeAsync(StoreDescriptor store, CancellationToken ct)
    {
        var shop = await FindShopAsync(store, ct);
        if (shop.CurrentFile is not { } file)
        {
            // Nothing to fingerprint. Force a fetch so the failure is reported every night.
            _logger.LogWarning("KAM store {StoreId}: the store list names no price-list file", store.ExternalId);
            return new SourceFingerprint("none:" + _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        }

        var uploaded = file.UploadedAt?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? "?";
        // No PublishedOn: the probe sees only the upload date, and the prices inside are older.
        return new SourceFingerprint($"file:{file.RelativePath}|uploaded:{uploaded}");
    }

    /// <exception cref="KamNoPriceListException">KAM published nothing usable for this store.</exception>
    /// <exception cref="KamFormatException">The list or PDF is not shaped as expected.</exception>
    public async Task<IReadOnlyList<RawPriceRecord>> FetchAsync(StoreDescriptor store, CancellationToken ct)
    {
        var shop = await FindShopAsync(store, ct);
        var file = shop.CurrentFile
            ?? throw new KamNoPriceListException($"KAM store {shop.Id} ({shop.Name}): the store list names no price-list file.");

        var uri = PriceListUri(file.RelativePath);
        await _http.PauseAsync(_options.RequestDelay, _options.RequestDelayJitter, ct);
        var pdf = await DownloadAsync(uri, ct);

        if (pdf.Length == 0 || !LooksLikePdf(pdf))
        {
            throw new KamNoPriceListException(
                $"KAM store {shop.Id} ({shop.Name}): {uri} returned {(pdf.Length == 0 ? "an empty body" : "something other than a PDF")}. " +
                "KAM answers missing files with HTTP 200 and no content.");
        }

        var table = KamTableReader.Read(KamPdfReader.Read(pdf), Layout);

        if (table.SnapshotAt is not { } snapshotAt)
        {
            // Without it the prices would show the ingest date, implying they are newer than
            // they are (KAM's lag ~2 days). Fail instead (CLAUDE.md: never imply live prices).
            throw new KamFormatException(
                $"KAM store {shop.Id}: no 'последно ажурирање на цените' stamp above the table in {uri}.");
        }

        if (table.Rows.Count == 0)
        {
            throw new KamNoPriceListException(
                $"KAM store {shop.Id} ({shop.Name}): {uri} has an empty price table, last updated {table.SnapshotText}.",
                snapshotAt);
        }

        if (table.TableGlyphs > 0 && (double)table.OrphanGlyphs / table.TableGlyphs > _options.MaxOrphanShare)
        {
            throw new KamFormatException(
                $"KAM store {shop.Id}: {table.OrphanGlyphs} of {table.TableGlyphs} glyphs fit no row or column in {uri}. " +
                "The table layout has probably changed.");
        }

        var publishedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(snapshotAt, SkopjeTime.Zone).DateTime);
        var records = new List<RawPriceRecord>(table.Rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var duplicates = 0;

        foreach (var row in table.Rows)
        {
            if (!KamRecordMapper.TryMap(row, publishedOn, out var record, out var reason))
            {
                skipped++;
                _logger.LogDebug("KAM {StoreId} page {Page}: skipped row ({Reason})", shop.Id, row.Page, reason);
                continue;
            }
            if (!seen.Add(record.SourceKey))
            {
                duplicates++;
                continue;
            }
            records.Add(record);
        }

        if (records.Count == 0)
        {
            throw new KamFormatException($"KAM store {shop.Id}: {table.Rows.Count} rows read from {uri}, none parseable.");
        }

        var age = SkopjeTime.Today(_time).DayNumber - publishedOn.DayNumber;
        if (age > _options.StaleAfter.TotalDays)
        {
            _logger.LogWarning("KAM store {StoreId}: price list is {Age} days old (stamped {Snapshot})",
                shop.Id, age, table.SnapshotText);
        }

        _logger.LogInformation(
            "KAM {StoreId}: {Count} records from {Pages} pages ({Skipped} skipped, {Duplicates} duplicate names, {Orphans} stray glyphs), published {PublishedOn}",
            shop.Id, records.Count, table.Pages, skipped, duplicates, table.OrphanGlyphs, publishedOn);

        return records;
    }

    private bool InScope(KamShop shop) =>
        shop.City is { } city && Cities.Any(c => string.Equals(c.Trim(), city, StringComparison.OrdinalIgnoreCase));

    private async Task<KamShop> FindShopAsync(StoreDescriptor store, CancellationToken ct)
    {
        var shops = await GetShopsAsync(ct);
        return shops.FirstOrDefault(s => s.Id.ToString(CultureInfo.InvariantCulture) == store.ExternalId)
               ?? throw new KamFormatException($"KAM store {store.ExternalId} is not in the store list any more.");
    }

    private async Task<IReadOnlyList<KamShop>> GetShopsAsync(CancellationToken ct)
    {
        await _shopListLock.WaitAsync(ct);
        try
        {
            if (_shopList is { } cached && _time.GetUtcNow() - cached.LoadedAt < _options.ShopListCacheFor)
            {
                return cached.Shops;
            }

            string json;
            using (var response = await _http.PostFormAsync(new Uri(_options.BaseUrl, _options.ShopListPath), [], ct))
            {
                json = await response.Content.ReadAsStringAsync(ct);
            }

            var shops = KamShopList.Parse(json);
            if (shops.Count == 0)
            {
                throw new KamFormatException("KAM's store list is empty.");
            }

            _shopList = (shops, _time.GetUtcNow());
            return shops;
        }
        finally
        {
            _shopListLock.Release();
        }
    }

    /// <summary>The path comes from KAM's JSON; accept only a plain relative path to a PDF on the same site.</summary>
    internal Uri PriceListUri(string relativePath)
    {
        var trimmed = relativePath.Trim();
        var path = trimmed.TrimStart('/');
        if (trimmed.StartsWith("//", StringComparison.Ordinal) // scheme-relative: another host
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal)
            || path.Contains('\\')
            || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new KamFormatException($"Unexpected price-list path '{relativePath}'.");
        }

        var uri = new Uri(_options.BaseUrl, path);
        if (uri.Host != _options.BaseUrl.Host)
        {
            throw new KamFormatException($"Price-list path '{relativePath}' leaves {_options.BaseUrl.Host}.");
        }
        return uri;
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _options.MaxPdfBytes)
            {
                throw new KamFormatException($"{uri} is larger than {_options.MaxPdfBytes} bytes; not a price list.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool LooksLikePdf(byte[] bytes) =>
        bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-';
}

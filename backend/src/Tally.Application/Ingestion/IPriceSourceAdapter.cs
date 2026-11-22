namespace Tally.Application.Ingestion;

/// <summary>
/// One per chain. REQUIREMENTS.md §5.1.
/// </summary>
/// <remarks>
/// <see cref="ProbeAsync"/> must be cheap (one request where possible). The worker calls
/// <see cref="FetchAsync"/> only when the fingerprint differs from the stored one, or on the
/// weekly forced re-parse.
/// </remarks>
public interface IPriceSourceAdapter
{
    string ChainCode { get; }

    Task<IReadOnlyList<StoreDescriptor>> DiscoverStoresAsync(CancellationToken ct);

    Task<SourceFingerprint> ProbeAsync(StoreDescriptor store, CancellationToken ct);

    Task<IReadOnlyList<RawPriceRecord>> FetchAsync(StoreDescriptor store, CancellationToken ct);
}

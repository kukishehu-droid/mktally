namespace Tally.Infrastructure.Ingestion.Kam;

/// <summary>
/// KAM's store list or PDF is not shaped as expected. Our parser needs attention.
/// </summary>
public sealed class KamFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// KAM published nothing usable for this store: an empty response where the PDF should be,
/// or a PDF whose table has no rows. Nothing to fix on our side; the store's existing
/// prices keep their old publication date and age honestly.
/// </summary>
/// <remarks>
/// Both cases were live on 2026-11-22: missing dated paths answer HTTP 200 with an empty
/// body (not 404), and store 69 (Струга - 5) published an empty table stamped 13.10.2026.
/// </remarks>
public sealed class KamNoPriceListException(string message, DateTimeOffset? snapshotAt = null) : Exception(message)
{
    /// <summary>The PDF's own "last updated" stamp, when there was a PDF.</summary>
    public DateTimeOffset? SnapshotAt { get; } = snapshotAt;
}

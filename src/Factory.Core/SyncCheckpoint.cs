namespace Factory.Core;

/// <summary>
/// Computes the checkpoint persisted as a repository's <c>last_synced_at</c> after a GitHub sync cycle. GitHub's
/// search API (used via <c>gh issue list --search</c>) is eventually consistent: an issue created or updated in
/// the seconds before a cycle starts can still be missing from that cycle's search results because it has not
/// been indexed yet. If the checkpoint simply advanced to the cycle's start time regardless, that issue would be
/// permanently skipped, because every future cycle only asks for <c>updated:>=</c> that now-advanced checkpoint.
/// Backdating the persisted checkpoint by a small safety margin re-includes that window in the next cycle instead.
/// </summary>
public static class SyncCheckpoint
{
    /// <summary>Larger than GitHub's typical search-indexing lag, small enough that the re-fetched window stays
    /// tiny even on a short polling interval.</summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(2);

    public static DateTimeOffset From(DateTimeOffset syncStartedAt) => syncStartedAt - SafetyMargin;
}

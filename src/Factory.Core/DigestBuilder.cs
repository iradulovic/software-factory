namespace Factory.Core;

/// <summary>Pure digest-generation logic (SF-705): no database or clock access of its own, so it is trivially
/// unit-testable. Given the currently-open alert candidates and finished work a caller already fetched, plus what
/// was already surfaced last generation (<see cref="IDigestStore.GetAlertFingerprintsAsync"/>), decides which
/// alerts are new or changed enough to include and which are unchanged noise to suppress.</summary>
public static class DigestBuilder
{
    public static DigestPayload Build(
        DateTimeOffset windowSince, DateTimeOffset windowUntil,
        IReadOnlyList<DigestFinishedTask> finishedWork,
        IReadOnlyList<DigestAlertCandidate> ciFailures,
        IReadOnlyList<DigestAlertCandidate> needsHuman,
        IReadOnlyList<DigestAlertCandidate> blockers,
        IReadOnlyDictionary<string, string> previousFingerprints) => new(
            windowSince, windowUntil,
            finishedWork,
            Surfaced(ciFailures, previousFingerprints), ciFailures.Count,
            Surfaced(needsHuman, previousFingerprints), needsHuman.Count,
            Surfaced(blockers, previousFingerprints), blockers.Count);

    private static IReadOnlyList<DigestAlertCandidate> Surfaced(IReadOnlyList<DigestAlertCandidate> candidates, IReadOnlyDictionary<string, string> previousFingerprints)
        => candidates.Where(c => !previousFingerprints.TryGetValue(c.Key, out var previous) || previous != Fingerprint(c))
            .OrderByDescending(c => c.UpdatedAt).ToList();

    /// <summary>A stable content fingerprint for one alert candidate — deliberately just its own detail text
    /// (already distinguishes "still failing the same way" from "failing differently now"), not a cryptographic
    /// hash: there is no security property to preserve here, and a hash would only make
    /// <c>factory.digest_alert_state</c> harder to read by hand.</summary>
    public static string Fingerprint(DigestAlertCandidate candidate) => candidate.Detail;
}

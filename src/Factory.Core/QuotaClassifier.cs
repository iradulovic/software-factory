using System.Globalization;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Where a detected quota exhaustion sits on the calendar: unclassified (no signal at all), a short
/// cooldown, a longer weekly-scale restriction, or a signal whose window this classifier cannot place (kept
/// distinct so nothing downstream mistakes it for a definite short-term reset).</summary>
public enum QuotaWindow { None, ShortTerm, Weekly, Unknown }

/// <summary>How confidently a detected quota exhaustion's reset time is known. <see cref="Reported"/> means the
/// CLI's own output stated a reset and it parsed cleanly. <see cref="Estimated"/> means no such statement was
/// found (or the profile has no pattern configured to look for one) and the profile's own configured cooldown was
/// used as a bounded guess. <see cref="Unknown"/> means a reset statement was present but could not be parsed, so
/// the same bounded cooldown is used but is never presented as a value the CLI actually reported.</summary>
public enum QuotaResetKind { None, Reported, Estimated, Unknown }

/// <summary>One agent invocation's quota signal. <see cref="Detail"/> is only ever the short, configured signature
/// string that triggered detection — never the full process output (already preserved separately on the agent
/// run record) — so it can never carry a credential a broken CLI happened to dump to stderr.</summary>
public sealed record QuotaSignal(bool Detected, QuotaWindow Window, QuotaResetKind ResetKind, DateTimeOffset? ResetAt, string? Detail)
{
    public static readonly QuotaSignal None = new(false, QuotaWindow.None, QuotaResetKind.None, null, null);
}

/// <summary>
/// Decides, from one agent invocation's process result and its <see cref="AgentProfile"/>, whether the agent hit
/// a quota limit and how confidently its reset time is known. Every rule here reads only configuration on
/// <see cref="AgentProfile"/>, never a hardcoded agent name, so a new CLI agent's quota behavior is a
/// configuration change (AGENTS.md's agent-boundary rule).
/// </summary>
public static class QuotaClassifier
{
    /// <summary>A successful invocation is never evidence of quota exhaustion, however its output reads: an agent
    /// that ran to completion and exited cleanly was plainly not blocked, and its stdout can legitimately mention
    /// a signature word while narrating unrelated work (implementing a feature about quotas, for example).
    /// Evidence is read from stderr alone, the channel CLI tools actually report their own errors on — never the
    /// agent's own narrated stdout.</summary>
    public static QuotaSignal Classify(AgentProfile profile, ProcessResult process, DateTimeOffset now)
    {
        if (process.Succeeded) return QuotaSignal.None;
        var evidence = process.StandardError;

        var weeklySignature = FirstMatch(profile.WeeklyQuotaSignatures, evidence);
        if (weeklySignature is not null)
            return Resolve(profile, evidence, weeklySignature, QuotaWindow.Weekly, profile.WeeklyQuotaCooldownHours, now);

        var genericSignature = FirstMatch(profile.QuotaSignatures, evidence);
        if (genericSignature is not null)
            return Resolve(profile, evidence, genericSignature, QuotaWindow.ShortTerm, profile.QuotaCooldownHours, now);

        return QuotaSignal.None;
    }

    private static QuotaSignal Resolve(AgentProfile profile, string evidence, string matchedSignature, QuotaWindow window, int boundedCooldownHours, DateTimeOffset now)
    {
        if (profile.QuotaResetPattern is { Length: > 0 } pattern)
        {
            var reported = TryExtractExplicitReset(pattern, evidence, now);
            if (reported is { } reset) return new QuotaSignal(true, window, QuotaResetKind.Reported, reset, matchedSignature);
            if (PatternMatched(pattern, evidence))
                return new QuotaSignal(true, window, QuotaResetKind.Unknown, now.AddHours(boundedCooldownHours), matchedSignature);
        }
        return new QuotaSignal(true, window, QuotaResetKind.Estimated, now.AddHours(boundedCooldownHours), matchedSignature);
    }

    private static string? FirstMatch(IReadOnlyList<string>? signatures, string evidence) =>
        signatures?.FirstOrDefault(signature => evidence.Contains(signature, StringComparison.OrdinalIgnoreCase));

    private static bool PatternMatched(string pattern, string evidence)
    {
        try { return Regex.IsMatch(evidence, pattern, RegexOptions.IgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Extracts and parses the named "value" capture group from <paramref name="pattern"/>, or
    /// <see langword="null"/> if the pattern is invalid, does not match, has no such group, or the captured text
    /// does not parse as a recognizable reset expression.</summary>
    private static DateTimeOffset? TryExtractExplicitReset(string pattern, string evidence, DateTimeOffset now)
    {
        Match match;
        try { match = Regex.Match(evidence, pattern, RegexOptions.IgnoreCase); }
        catch (ArgumentException) { return null; }
        if (!match.Success || !match.Groups["value"].Success) return null;
        return QuotaResetParser.TryParse(match.Groups["value"].Value.Trim(), now);
    }
}

/// <summary>Parses one captured reset expression into an absolute point in time: either an absolute timestamp, or
/// a relative duration ("5h", "3.5 hours", "2 days"). Returns <see langword="null"/> for anything else, so a
/// malformed or unrecognized value is never silently guessed at.</summary>
public static class QuotaResetParser
{
    private static readonly Regex Relative = new(
        @"^\s*(?<amount>\d+(\.\d+)?)\s*(?<unit>h|hr|hrs|hour|hours|m|min|mins|minute|minutes|d|day|days)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DateTimeOffset? TryParse(string value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var absolute))
            return absolute;

        var relative = Relative.Match(value);
        if (!relative.Success) return null;
        var amount = double.Parse(relative.Groups["amount"].Value, CultureInfo.InvariantCulture);
        var span = relative.Groups["unit"].Value.ToLowerInvariant()[0] switch
        {
            'h' => TimeSpan.FromHours(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => (TimeSpan?)null
        };
        return span is null ? null : now + span;
    }
}

/// <summary>Extracts a provider session/thread id from one invocation's stdout (SF-701), using
/// <see cref="AgentProfile.SessionIdPattern"/> — the same named-capture-group convention <see cref="QuotaResetParser"/>
/// already uses for quota reset text, applied here to a different field on the same profile.</summary>
public static class ProviderSessionExtractor
{
    public static string? TryExtract(AgentProfile profile, string standardOutput)
    {
        if (!profile.SupportsSessionResume || profile.SessionIdPattern is not { Length: > 0 } pattern) return null;
        Match match;
        try { match = Regex.Match(standardOutput, pattern); }
        catch (ArgumentException) { return null; }
        return match.Success && match.Groups["sessionId"].Success ? match.Groups["sessionId"].Value : null;
    }
}

namespace Factory.Core;

/// <summary>Whether a failed build/test command looks like a genuine code problem the agent could fix by trying
/// again (<see cref="Repairable"/>), or an environment/infrastructure problem no amount of code editing can fix
/// (<see cref="Operational"/>) — automatically re-invoking the agent for an operational failure would only waste
/// the implementation-attempt budget on repeats that could never succeed.</summary>
public enum ValidationFailureKind { Repairable, Operational }

/// <summary>
/// Classifies one failed build/test <see cref="ProcessResult"/> (SF-606). Deliberately narrow and conservative:
/// only signals that unambiguously indicate the environment itself is broken — never the agent's code — are
/// classified <see cref="ValidationFailureKind.Operational"/>. Everything else defaults to
/// <see cref="ValidationFailureKind.Repairable"/>, since most build/test failures genuinely are code problems,
/// and a wasted automatic repair attempt on a true operational failure is still bounded by the same
/// implementation-attempt budget as any other attempt — a false negative here costs one attempt, not a runaway loop.
/// </summary>
public static class ValidationFailureClassifier
{
    /// <summary>127 is the POSIX shell convention for "command not found" — a portable, locale-independent
    /// signal that the toolchain itself is missing, distinct from any text pattern.</summary>
    private const int CommandNotFoundExitCode = 127;

    private static readonly string[] OperationalSignatures =
    [
        "command not found", "is not recognized as an internal or external command", "no such file or directory",
        "permission denied", "unauthorized", "401", "403", "authentication failed", "could not resolve host",
        "name or service not known", "network is unreachable", "ssl certificate problem", "certificate verify failed"
    ];

    public static ValidationFailureKind Classify(ProcessResult process)
    {
        if (process.ExitCode == CommandNotFoundExitCode) return ValidationFailureKind.Operational;
        var evidence = process.StandardError + "\n" + process.StandardOutput;
        return OperationalSignatures.Any(signature => evidence.Contains(signature, StringComparison.OrdinalIgnoreCase))
            ? ValidationFailureKind.Operational
            : ValidationFailureKind.Repairable;
    }
}

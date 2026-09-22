namespace Factory.Core.Tests;

public sealed class ValidationFailureClassifierTests
{
    private static ProcessResult Process(int? exitCode, string stderr = "", string stdout = "")
    {
        var start = DateTimeOffset.UtcNow;
        return new ProcessResult("dotnet", ["test"], ".", start, start.AddSeconds(1), exitCode, stdout, stderr, false, false);
    }

    [Fact]
    public void A_plain_compile_or_test_failure_is_repairable()
    {
        var result = ValidationFailureClassifier.Classify(Process(1, stderr: "error CS0103: The name 'Foo' does not exist in the current context"));
        Assert.Equal(ValidationFailureKind.Repairable, result);
    }

    [Theory]
    [InlineData("bash: dotnet: command not found")]
    [InlineData("'dotnet' is not recognized as an internal or external command")]
    [InlineData("/bin/sh: 1: dotnet: No such file or directory")]
    [InlineData("Permission denied")]
    [InlineData("Response status code does not indicate success: 401 (Unauthorized).")]
    [InlineData("error: 403 Forbidden")]
    [InlineData("fatal: Authentication failed for 'https://example.invalid/repo.git'")]
    [InlineData("Could not resolve host: nuget.org")]
    [InlineData("Temporary failure in name resolution: Name or service not known")]
    [InlineData("connect ENETUNREACH: Network is unreachable")]
    [InlineData("SSL certificate problem: unable to get local issuer certificate")]
    [InlineData("certificate verify failed: unable to get local issuer certificate")]
    public void An_environment_signature_is_classified_operational(string stderr)
    {
        var result = ValidationFailureClassifier.Classify(Process(1, stderr: stderr));
        Assert.Equal(ValidationFailureKind.Operational, result);
    }

    [Fact]
    public void Exit_code_127_is_classified_operational_regardless_of_output()
    {
        var result = ValidationFailureClassifier.Classify(Process(127, stderr: "some unrelated message"));
        Assert.Equal(ValidationFailureKind.Operational, result);
    }

    [Fact]
    public void An_operational_signature_in_stdout_is_still_detected()
    {
        var result = ValidationFailureClassifier.Classify(Process(1, stdout: "npm ERR! 404 Not Found - could not resolve host: registry.invalid"));
        Assert.Equal(ValidationFailureKind.Operational, result);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var result = ValidationFailureClassifier.Classify(Process(1, stderr: "COMMAND NOT FOUND"));
        Assert.Equal(ValidationFailureKind.Operational, result);
    }
}

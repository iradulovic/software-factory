using Factory.Core;

namespace Factory.Infrastructure.Tests;

public sealed class RepositoryConfigurationReaderTests
{
    [Fact]
    public void Partial_configuration_merges_with_defaults()
    {
        var configuration = RepositoryConfigurationReader.Parse("""{"buildCommands":["dotnet build --nologo"],"maxImplementationAttempts":3}""", "origin/main");

        Assert.Equal([new ValidationCommand("dotnet", ["build", "--nologo"])], configuration.BuildCommands);
        Assert.Equal(RepositoryConfiguration.Default.TestCommands, configuration.TestCommands);
        Assert.Equal(3, configuration.MaxImplementationAttempts);
        Assert.Equal(RepositoryConfiguration.Default.MaxReviewAttempts, configuration.MaxReviewAttempts);
        Assert.Equal("main", configuration.BaseBranch);
        Assert.True(configuration.RequireHumanMerge);
        Assert.Equal("manual", configuration.Publish);
    }

    [Fact]
    public void Smoke_test_is_absent_by_default()
    {
        var configuration = RepositoryConfigurationReader.Parse("{}", "origin/main");

        Assert.Null(configuration.SmokeTest);
    }

    [Fact]
    public void Smoke_test_is_parsed_with_its_own_defaults_when_only_the_required_fields_are_set()
    {
        var configuration = RepositoryConfigurationReader.Parse("""
            {"smokeTest":{"startCommand":"npm run start","healthCheckUrl":"http://localhost:3000/health"}}
            """, "origin/main");

        Assert.NotNull(configuration.SmokeTest);
        Assert.Equal(new ValidationCommand("npm", ["run", "start"]), configuration.SmokeTest.StartCommand);
        Assert.Equal("http://localhost:3000/health", configuration.SmokeTest.HealthCheckUrl);
        Assert.Equal(["/"], configuration.SmokeTest.CheckPaths);
        Assert.Equal(60, configuration.SmokeTest.StartupTimeoutSeconds);
        Assert.Equal(30, configuration.SmokeTest.CheckTimeoutSeconds);
        Assert.Null(configuration.SmokeTest.InstallCommand);
    }

    [Fact]
    public void Smoke_test_install_command_is_read_when_set()
    {
        var configuration = RepositoryConfigurationReader.Parse("""
            {"smokeTest":{"installCommand":"npm ci","startCommand":"npm run start","healthCheckUrl":"http://localhost:3000/health"}}
            """, "origin/main");

        Assert.Equal(new ValidationCommand("npm", ["ci"]), configuration.SmokeTest!.InstallCommand);
    }

    [Fact]
    public void Smoke_test_check_paths_and_timeouts_are_read_when_set()
    {
        var configuration = RepositoryConfigurationReader.Parse("""
            {"smokeTest":{"startCommand":"npm run start","healthCheckUrl":"http://localhost:3000/health",
              "checkPaths":["/","/orders"],"startupTimeoutSeconds":90,"checkTimeoutSeconds":15}}
            """, "origin/main");

        Assert.Equal(["/", "/orders"], configuration.SmokeTest!.CheckPaths);
        Assert.Equal(90, configuration.SmokeTest.StartupTimeoutSeconds);
        Assert.Equal(15, configuration.SmokeTest.CheckTimeoutSeconds);
    }

    [Theory]
    [InlineData("""{"smokeTest":{"healthCheckUrl":"http://localhost:3000"}}""")]
    [InlineData("""{"smokeTest":{"startCommand":"npm run start"}}""")]
    [InlineData("""{"smokeTest":{"startCommand":"npm run start","healthCheckUrl":"http://localhost:3000","startupTimeoutSeconds":0}}""")]
    [InlineData("""{"smokeTest":{"startCommand":"npm run start","healthCheckUrl":"http://localhost:3000","checkTimeoutSeconds":0}}""")]
    [InlineData("""{"smokeTest":{"startCommand":"npm run start","healthCheckUrl":"http://localhost:3000","installCommand":{"shell":""}}}""")]
    public void Invalid_smoke_test_configuration_fails_clearly(string json) =>
        Assert.Contains(".factory/config.json", Assert.Throws<InvalidOperationException>(() => RepositoryConfigurationReader.Parse(json, "origin/main")).Message);

    [Fact]
    public void Publish_policy_is_read_and_trimmed()
    {
        var configuration = RepositoryConfigurationReader.Parse("""{"publish":" auto-draft "}""", "origin/main");

        Assert.Equal("auto-draft", configuration.Publish);
    }

    [Fact]
    public void Max_quota_interruptions_is_read_and_defaulted()
    {
        var configured = RepositoryConfigurationReader.Parse("""{"maxQuotaInterruptions":5}""", "origin/main");
        var defaulted = RepositoryConfigurationReader.Parse("{}", "origin/main");

        Assert.Equal(5, configured.MaxQuotaInterruptions);
        Assert.Equal(RepositoryConfiguration.Default.MaxQuotaInterruptions, defaulted.MaxQuotaInterruptions);
    }

    [Theory]
    [InlineData("""{"maxImplementationAttempts":0}""")]
    [InlineData("""{"maxQuotaInterruptions":0}""")]
    [InlineData("""{"testCommands":["dotnet test",""]}""")]
    [InlineData("""{"testCommands":[[]]}""")]
    [InlineData("""{"testCommands":[[""]]}""")]
    [InlineData("""{"testCommands":[{"shell":""}]}""")]
    [InlineData("""{"testCommands":[{"shell":"   "}]}""")]
    [InlineData("""{"testCommands":[123]}""")]
    [InlineData("""{"publish":"auto-merge"}""")]
    [InlineData("not json")]
    public void Invalid_configuration_fails_clearly(string json) =>
        Assert.Contains(".factory/config.json", Assert.Throws<InvalidOperationException>(() => RepositoryConfigurationReader.Parse(json, "origin/main")).Message);

    [Fact]
    public void Array_shaped_commands_preserve_an_argument_containing_spaces_without_a_shell()
    {
        var configuration = RepositoryConfigurationReader.Parse(
            """{"testCommands":[["dotnet","test","--filter","My Test With Spaces"]]}""", "origin/main");

        Assert.Equal([new ValidationCommand("dotnet", ["test", "--filter", "My Test With Spaces"])], configuration.TestCommands);
    }

    [Fact]
    public void Legacy_string_commands_split_on_whitespace_and_cannot_represent_a_spaced_argument()
    {
        var configuration = RepositoryConfigurationReader.Parse("""{"buildCommands":["dotnet build --nologo"]}""", "origin/main");

        Assert.Equal([new ValidationCommand("dotnet", ["build", "--nologo"])], configuration.BuildCommands);
    }

    [Fact]
    public void Shell_opt_in_commands_resolve_to_a_literal_shell_invocation()
    {
        var configuration = RepositoryConfigurationReader.Parse(
            """{"buildCommands":[{"shell":"dotnet build && dotnet test"}]}""", "origin/main");

        var expected = OperatingSystem.IsWindows()
            ? new ValidationCommand("cmd.exe", ["/c", "dotnet build && dotnet test"])
            : new ValidationCommand("/bin/sh", ["-c", "dotnet build && dotnet test"]);
        Assert.Equal([expected], configuration.BuildCommands);
    }

    [Fact]
    public void Shell_opt_in_is_the_only_way_a_shell_is_ever_used()
    {
        var configuration = RepositoryConfigurationReader.Parse("""{"buildCommands":["dotnet build"],"testCommands":[["dotnet","test"]]}""", "origin/main");

        Assert.All(configuration.BuildCommands.Concat(configuration.TestCommands),
            command => Assert.DoesNotContain(command.Executable, new[] { "/bin/sh", "cmd.exe" }));
    }

    [Fact]
    public async Task Configuration_is_read_from_the_base_reference_not_the_worktree()
    {
        var runner = new StubRunner(0, """{"testCommands":["npm test"]}""", "");
        var configuration = await new RepositoryConfigurationReader(runner).ReadAsync("/worktrees/issue-1", "origin/main", CancellationToken.None);

        Assert.Equal([new ValidationCommand("npm", ["test"])], configuration.TestCommands);
        Assert.NotNull(runner.Request);
        Assert.Equal("git", runner.Request.FileName);
        Assert.Equal(new[] { "show", "origin/main:.factory/config.json" }, runner.Request.Arguments);
        Assert.Equal("/worktrees/issue-1", runner.Request.WorkingDirectory);
    }

    [Fact]
    public async Task Missing_configuration_at_the_base_reference_uses_defaults()
    {
        var runner = new StubRunner(128, "", "fatal: path '.factory/config.json' does not exist in 'origin/main'");
        Assert.Equal(RepositoryConfiguration.Default, await new RepositoryConfigurationReader(runner).ReadAsync(".", "origin/main", CancellationToken.None));
    }

    [Fact]
    public async Task Unreadable_base_reference_fails_instead_of_silently_using_defaults()
    {
        var runner = new StubRunner(128, "", "fatal: invalid object name 'origin/nope'.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new RepositoryConfigurationReader(runner).ReadAsync(".", "origin/nope", CancellationToken.None));
        Assert.Contains("origin/nope", error.Message);
    }

    [Fact]
    public async Task Task_context_writer_removes_a_stale_agent_result()
    {
        var root = Directory.CreateTempSubdirectory("factory-context-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"), "{\"status\":\"completed\"}");
            var task = new FactoryTask(Guid.NewGuid(), 1, 2, 7, "Title", "Body", "GitHubIssue", 0, FactoryTaskStatus.Preparing, null, "main",
                null, null, null, null, null, DateTimeOffset.UtcNow, null, null, null, null);

            await new TaskContextWriter().WriteAsync(root.FullName, new GitHubRepository(1, "acme", "billing", "url", "main", true), null, task,
                new AttemptContext(1, 2, null), CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(directory.FullName, "result.json")));
            var content = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "task.md"));
            Assert.Contains("GitHub issue: #7", content);
            Assert.Contains("This is attempt 1 of 2.", content);
            Assert.DoesNotContain("Previous attempt", content);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Task_context_writer_includes_the_previous_attempts_summary_when_repeating()
    {
        var root = Directory.CreateTempSubdirectory("factory-context-");
        try
        {
            var task = new FactoryTask(Guid.NewGuid(), 1, 2, 7, "Title", "Body", "GitHubIssue", 0, FactoryTaskStatus.Preparing, null, "main",
                null, null, null, null, null, DateTimeOffset.UtcNow, null, null, null, null);
            var previous = new PreviousAttemptSummary("Implemented the wrong endpoint", "Test failed: assertion mismatch", ["src/A.cs", "src/B.cs"], 4, 2);

            await new TaskContextWriter().WriteAsync(root.FullName, new GitHubRepository(1, "acme", "billing", "url", "main", true), null, task,
                new AttemptContext(2, 3, previous), CancellationToken.None);

            var content = await File.ReadAllTextAsync(Path.Combine(root.FullName, ".factory", "task.md"));
            Assert.Contains("This is attempt 2 of 3.", content);
            Assert.Contains("## Previous attempt", content);
            Assert.Contains("Implemented the wrong endpoint", content);
            Assert.Contains("Test failed: assertion mismatch", content);
            Assert.Contains("src/A.cs, src/B.cs", content);
        }
        finally { root.Delete(true); }
    }

    private sealed class StubRunner(int exitCode, string stdout, string stderr) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProcessResult(request.FileName, request.Arguments, request.WorkingDirectory, now, now, exitCode, stdout, stderr, false, false));
        }
    }
}

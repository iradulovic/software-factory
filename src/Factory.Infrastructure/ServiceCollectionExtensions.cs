using Dapper;
using Factory.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Infrastructure;

/// <summary>Npgsql reads a <c>timestamptz</c> column as <see cref="DateTime"/>, but Dapper's constructor-based
/// (record) materialization requires an exact type match and has no built-in <see cref="DateTime"/> -&gt;
/// <see cref="DateTimeOffset"/> conversion for that path (only for mutable POCO property setters) - without this
/// handler, any Dapper query into a record with a <see cref="DateTimeOffset"/> parameter throws "no matching
/// constructor" for every row, regardless of its actual values.</summary>
internal sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateTimeOffset.")
    };

    public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value) => parameter.Value = value;
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        services.Configure<FactoryOptions>(configuration.GetSection("Factory"));
        services.Configure<GitHubSyncOptions>(configuration.GetSection("GitHub"));
        services.Configure<WorktreeCleanupOptions>(configuration.GetSection("WorktreeCleanup"));
        services.Configure<DigestOptions>(configuration.GetSection("Digest"));
        services.Configure<AssistantOptions>(configuration.GetSection("Assistant"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IAgentResultReader, AgentResultReader>();
        services.AddSingleton<IAgentReviewResultReader, AgentReviewResultReader>();
        services.AddSingleton<IGitHubAvailabilityChecker, GitHubAvailabilityChecker>();

        // One IAgentRunner/IAgentAvailabilityChecker per configured agent profile: adding a CLI coding agent is
        // a configuration change (see AgentProfilesOptions), never a new class or a registration here.
        var profiles = configuration.GetSection("Agents").Get<AgentProfilesOptions>()?.Profiles is { Count: > 0 } configured
            ? configured : AgentProfilesOptions.DefaultProfiles;
        foreach (var profile in profiles)
        {
            services.AddSingleton<IAgentRunner>(sp => new CliAgentRunner(profile, sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IAgentResultReader>(),
                sp.GetRequiredService<IAgentReviewResultReader>(), sp.GetRequiredService<IClock>()));
            services.AddSingleton<IAgentAvailabilityChecker>(sp => new CliAgentAvailabilityChecker(profile, sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IClock>()));
        }

        services.AddSingleton<IRepositoryCache, RepositoryCache>();
        services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
        services.AddSingleton<IWorktreeInspector, GitWorktreeInspector>();
        services.AddSingleton<IRepositoryConfigurationReader, RepositoryConfigurationReader>();
        services.AddSingleton<IEnvironmentVariableReader, EnvironmentVariableReader>();
        services.AddSingleton<IDeploymentProvider, VercelDeploymentProvider>();
        services.AddSingleton<IDeploymentProvider, SupabaseDeploymentProvider>();
        services.AddSingleton<IDeploymentStore, PostgresDeploymentStore>();
        services.AddSingleton<IDeploymentProvisioner, DeploymentProvisioner>();
        services.AddSingleton<IFactoryReleaseStore, PostgresFactoryReleaseStore>();
        services.AddSingleton<IFactoryReleaseVersionStore>(sp => (IFactoryReleaseVersionStore)sp.GetRequiredService<IFactoryReleaseStore>());
        services.AddSingleton<IRepositoryReleaseVersionHistoryReader, GhCliRepositoryReleaseVersionHistoryReader>();
        services.AddSingleton<ITaskContextWriter, TaskContextWriter>();
        services.AddSingleton<IGitHubClient, GhCliClient>();
        services.AddSingleton<IGitHubPublisher, GhCliPublisher>();
        services.AddSingleton<IIssueReadyLabelWriter, GhCliIssueReadyLabelWriter>();
        services.AddSingleton<IReleaseIssueWriter, GhCliReleaseIssueWriter>();
        services.AddSingleton<IRepositoryBootstrapper, RepositoryBootstrapper>();
        services.AddSingleton<IGitHubStore, PostgresGitHubStore>();
        services.AddSingleton<ITaskStore, PostgresTaskStore>();
        services.AddSingleton<IDigestStore, PostgresDigestStore>();
        services.AddSingleton<IBrowserSmokeTestRunner, PlaywrightSmokeTestRunner>();
        services.AddSingleton<DatabaseMigrator>();
        return services;
    }
}

using Factory.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFactoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        services.Configure<FactoryOptions>(configuration.GetSection("Factory"));
        services.Configure<GitHubSyncOptions>(configuration.GetSection("GitHub"));
        services.Configure<WorktreeCleanupOptions>(configuration.GetSection("WorktreeCleanup"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IAgentResultReader, AgentResultReader>();

        // One IAgentRunner/IAgentAvailabilityChecker per configured agent profile: adding a CLI coding agent is
        // a configuration change (see AgentProfilesOptions), never a new class or a registration here.
        var profiles = configuration.GetSection("Agents").Get<AgentProfilesOptions>()?.Profiles is { Count: > 0 } configured
            ? configured : new AgentProfilesOptions().Profiles;
        foreach (var profile in profiles)
        {
            services.AddSingleton<IAgentRunner>(sp => new CliAgentRunner(profile, sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<IAgentResultReader>(), sp.GetRequiredService<IClock>()));
            services.AddSingleton<IAgentAvailabilityChecker>(sp => new CliAgentAvailabilityChecker(profile, sp.GetRequiredService<IProcessRunner>()));
        }

        services.AddSingleton<IRepositoryCache, RepositoryCache>();
        services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
        services.AddSingleton<IWorktreeInspector, GitWorktreeInspector>();
        services.AddSingleton<IRepositoryConfigurationReader, RepositoryConfigurationReader>();
        services.AddSingleton<ITaskContextWriter, TaskContextWriter>();
        services.AddSingleton<IGitHubClient, GhCliClient>();
        services.AddSingleton<IGitHubPublisher, GhCliPublisher>();
        services.AddSingleton<IGitHubStore, PostgresGitHubStore>();
        services.AddSingleton<ITaskStore, PostgresTaskStore>();
        services.AddSingleton<DatabaseMigrator>();
        return services;
    }
}

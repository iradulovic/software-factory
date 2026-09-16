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
        services.Configure<CodexOptions>(configuration.GetSection("Codex"));
        services.Configure<GitHubSyncOptions>(configuration.GetSection("GitHub"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IAgentResultReader, AgentResultReader>();
        services.AddSingleton<IAgentRunner, CodexAgentRunner>();
        services.AddSingleton<IRepositoryCache, RepositoryCache>();
        services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
        services.AddSingleton<IRepositoryConfigurationReader, RepositoryConfigurationReader>();
        services.AddSingleton<ITaskContextWriter, TaskContextWriter>();
        services.AddSingleton<IGitHubClient, GhCliClient>();
        services.AddSingleton<IGitHubStore, PostgresGitHubStore>();
        services.AddSingleton<ITaskStore, PostgresTaskStore>();
        services.AddSingleton<DatabaseMigrator>();
        return services;
    }
}

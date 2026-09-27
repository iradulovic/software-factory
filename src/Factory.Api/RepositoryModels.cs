public sealed record AddRepositoryRequest(string Owner, string Name, string? CloneUrl, string? DefaultBranch);

public sealed record BootstrapRepositoryRequest(
    string Owner,
    string Name,
    string ProductName,
    string FirstJourney,
    string BackendChoice,
    string AuthenticationProvider,
    string DeployTarget,
    string Shell,
    string? Visibility = null,
    string? ExistingRepositoryUrl = null);

public sealed record SetRepositoryEnabledRequest(bool IsEnabled);

public sealed record ProvisionDeploymentRequest(string Provider);

public sealed record SetIssueReadyRequest(bool IsReady);

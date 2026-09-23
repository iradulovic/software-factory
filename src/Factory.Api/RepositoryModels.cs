public sealed record AddRepositoryRequest(string Owner, string Name, string? CloneUrl, string? DefaultBranch);

public sealed record SetRepositoryEnabledRequest(bool IsEnabled);

public sealed record PriorityRequest(int Priority);

public sealed record DependencyRequest(Guid DependsOnTaskId);

public sealed record ContinueRequest(string Feedback);
public sealed record ResolveAgentRequest(string RequestId, string Resolution, string Answer, string? BranchName, string? HeadCommit);
public sealed record LegacyVerificationRequest(string Checks, string BranchName, string HeadCommit);

public sealed record ReviewTimeRequest(int Minutes);

public sealed record PriorityRequest(int Priority);

public sealed record DependencyRequest(Guid DependsOnTaskId);

public sealed record ContinueRequest(string Feedback);

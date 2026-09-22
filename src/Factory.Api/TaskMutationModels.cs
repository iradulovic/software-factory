public sealed record PriorityRequest(int Priority);

public sealed record DependencyRequest(Guid DependsOnTaskId);

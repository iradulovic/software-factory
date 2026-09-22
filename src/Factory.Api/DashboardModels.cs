public sealed class DashboardMetricsRow
{
    public int ActiveTasks { get; init; }
    public int PendingTasks { get; init; }
    public int CompletedToday { get; init; }
    public int NeedsOperator { get; init; }
    public int ReviewBacklog { get; init; }
    public decimal SuccessRate { get; init; }
}

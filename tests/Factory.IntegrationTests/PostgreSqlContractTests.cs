namespace Factory.IntegrationTests;

public sealed class PostgreSqlContractTests
{
    [Fact]
    public void Migration_prevents_duplicate_active_tasks_for_an_issue()
    {
        var sql = ReadMigration();
        Assert.Contains("ux_factory_task_active_issue", sql);
        Assert.Contains("status NOT IN ('Completed', 'Failed', 'Cancelled')", sql);
    }

    [Fact]
    public void Claiming_uses_skip_locked_and_updates_the_candidate()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Factory.Infrastructure", "Database.cs"));
        Assert.Contains("FOR UPDATE SKIP LOCKED", source);
        Assert.Contains("UPDATE factory.task t SET status='Claimed'", source);
    }

    [Fact]
    public void Agent_result_migration_preserves_structured_execution_evidence()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "database", "migrations", "002_agent_result.sql"));
        Assert.Contains("result_json JSONB", sql);
        Assert.Contains("files_changed JSONB", sql);
        Assert.Contains("risks JSONB", sql);
        Assert.Contains("human_reason TEXT", sql);
    }

    [Fact]
    public void Lease_recovery_has_a_partial_expiry_index()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "database", "migrations", "003_task_lease_recovery.sql"));
        Assert.Contains("ix_factory_task_expired_lease", sql);
        Assert.Contains("lease_until", sql);
        Assert.Contains("Implementing", sql);
    }

    [Fact]
    public void Deployment_registry_is_repository_scoped_and_provider_unique()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "database", "migrations", "037_deployment_registry.sql"));
        Assert.Contains("CREATE TABLE factory.deployment", sql);
        Assert.Contains("REFERENCES github.repository(id)", sql);
        Assert.Contains("UNIQUE(repository_id,provider)", sql);
        Assert.Contains("linkage_metadata JSONB", sql);
    }

    [Fact]
    public void Release_plan_migration_persists_proposals_decisions_dependencies_and_reconciliation_evidence()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "database", "migrations", "038_release_plans.sql"));

        Assert.Contains("CREATE TABLE factory.release_plan", sql);
        Assert.Contains("CREATE TABLE factory.release_plan_item", sql);
        Assert.Contains("acceptance_criteria jsonb", sql);
        Assert.Contains("CREATE TABLE factory.release_plan_item_dependency", sql);
        Assert.Contains("CREATE TABLE factory.release_plan_decision", sql);
        Assert.Contains("CREATE TABLE factory.release_plan_evidence", sql);
        Assert.Contains("UNIQUE(item_id, kind, reference)", sql);
        Assert.Contains("CHECK (status IN ('Proposed','Approved','Active','Promoted'))", sql);
    }

    [Fact]
    public void Factory_release_migration_persists_branch_state_and_issue_membership_separately_from_tasks()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "database", "migrations", "039_factory_releases.sql"));
        Assert.Contains("CREATE TABLE factory.release", sql);
        Assert.Contains("UNIQUE(repository_id, release_number)", sql);
        Assert.Contains("CREATE TABLE factory.release_issue", sql);
        Assert.Contains("REFERENCES github.issue(id)", sql);
        Assert.Contains("github_milestone_id bigint", sql);
        Assert.Contains("branch_created_at timestamptz", sql);
        var taskStore = File.ReadAllText(Path.Combine(Root(), "src", "Factory.Infrastructure", "Database.cs"));
        Assert.Contains("'Pending','Creating','Failed'", taskStore);
        Assert.Contains("rel.status='Active'", taskStore);
        Assert.Contains("factory:ready", taskStore);
    }

    [Fact]
    public void Repository_release_version_migration_keeps_policy_history_and_planned_reason_separate()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "database", "migrations", "042_repository_release_versions.sql"));
        Assert.Contains("CREATE TABLE factory.repository_version_policy", sql);
        Assert.Contains("CREATE TABLE factory.repository_published_version", sql);
        Assert.Contains("CREATE TABLE factory.repository_version_reconciliation", sql);
        Assert.Contains("version_reason", sql);
        Assert.Contains("version_override_reason", sql);
        Assert.Contains("MAJOR.MINOR.PATCH", sql);
        Assert.Contains("tag_prefix text NOT NULL DEFAULT 'v'", sql);
    }

    [Fact]
    public void Review_policy_migration_persists_verdict_score_disposition_and_medium_impact()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "database", "migrations", "040_review_policy.sql"));
        Assert.Contains("CREATE TABLE factory.agent_review", sql);
        Assert.Contains("score smallint", sql);
        Assert.Contains("score_rationale text", sql);
        Assert.Contains("disposition text NOT NULL", sql);
        Assert.Contains("review_id uuid REFERENCES factory.agent_review", sql);
        Assert.Contains("medium_impact text", sql);
        Assert.Contains("rationale text", sql);
    }

    private static string ReadMigration() => File.ReadAllText(Path.Combine(Root(), "database", "migrations", "001_initial.sql"));
    private static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SoftwareFactory.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

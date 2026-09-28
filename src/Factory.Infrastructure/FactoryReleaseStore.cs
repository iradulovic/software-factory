using Dapper;
using Factory.Core;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace Factory.Infrastructure;

public sealed class PostgresFactoryReleaseStore(IOptions<FactoryOptions> options) : IFactoryReleaseStore, IFactoryReleaseVersionStore
{
    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    public async Task<IReadOnlyList<FactoryRelease>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        var rows = (await connection.QueryAsync<ReleaseRow>(new CommandDefinition(ReleaseSelect +
            " ORDER BY fr.created_at DESC, fr.id", cancellationToken: cancellationToken))).AsList();
        return await AddIssuesAsync(connection, rows, cancellationToken);
    }

    public async Task<FactoryRelease?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        var row = await connection.QuerySingleOrDefaultAsync<ReleaseRow>(new CommandDefinition(
            ReleaseSelect + " WHERE fr.id=@id", new { id }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var releases = await AddIssuesAsync(connection, [row], cancellationToken);
        return releases[0];
    }

    public async Task<RepositoryReleaseVersionState> GetVersionStateAsync(long repositoryId, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.repository_version_policy(repository_id)
            VALUES(@repositoryId) ON CONFLICT(repository_id) DO NOTHING
            """, new { repositoryId }, cancellationToken: cancellationToken));
        var policy = await connection.QuerySingleAsync<VersionPolicyRow>(new CommandDefinition("""
            SELECT version_format AS "VersionFormat",tag_prefix AS "TagPrefix",
              breaking_change_definition AS "BreakingChangeDefinition"
            FROM factory.repository_version_policy WHERE repository_id=@repositoryId
            """, new { repositoryId }, cancellationToken: cancellationToken));
        var publishedVersions = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT version FROM factory.repository_published_version WHERE repository_id=@repositoryId
            ORDER BY version
            """, new { repositoryId }, cancellationToken: cancellationToken))).AsList();
        var reconciliation = await connection.QuerySingleOrDefaultAsync<VersionReconciliationRow>(new CommandDefinition("""
            SELECT observed_tags AS "ObservedTags",observed_release_tags AS "ObservedReleaseTags",
              accepted_versions AS "AcceptedVersions",reason AS "Reason",reconciled_at AS "ReconciledAt"
            FROM factory.repository_version_reconciliation WHERE repository_id=@repositoryId
            ORDER BY reconciled_at DESC,id DESC LIMIT 1
            """, new { repositoryId }, cancellationToken: cancellationToken));
        var planned = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT release_number FROM factory.release WHERE repository_id=@repositoryId
              AND status IN ('Pending','Creating','Active','Failed') ORDER BY created_at DESC
            """, new { repositoryId }, cancellationToken: cancellationToken))).AsList();
        return new RepositoryReleaseVersionState(policy.VersionFormat, policy.TagPrefix, policy.BreakingChangeDefinition,
            publishedVersions, reconciliation?.ToModel(), planned);
    }

    public async Task ReconcileVersionHistoryAsync(long repositoryId, IReadOnlyList<string> observedTags,
        IReadOnlyList<string> observedReleaseTags, IReadOnlyList<string> acceptedVersions, string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@repositoryId)", new { repositoryId }, transaction, cancellationToken: cancellationToken));
        var existingVersions = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT version FROM factory.repository_published_version WHERE repository_id=@repositoryId FOR UPDATE
            """, new { repositoryId }, transaction, cancellationToken: cancellationToken))).AsList();
        var allAccepted = existingVersions.Concat(acceptedVersions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var version in allAccepted)
        {
            if (!SemanticReleaseVersion.TryParse(version, out _))
                throw new InvalidOperationException($"'{version}' is not a valid MAJOR.MINOR.PATCH version.");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO factory.repository_published_version(repository_id,version,source)
                VALUES(@repositoryId,@version,'OperatorConfirmed') ON CONFLICT(repository_id,version) DO NOTHING
                """, new { repositoryId, version }, transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.repository_version_reconciliation(id,repository_id,observed_tags,observed_release_tags,accepted_versions,reason)
            VALUES(@id,@repositoryId,@observedTags,@observedReleaseTags,@acceptedVersions,@reason)
            """, new
        {
            id = Guid.NewGuid(), repositoryId,
            observedTags = observedTags.Order(StringComparer.Ordinal).ToArray(),
            observedReleaseTags = observedReleaseTags.Order(StringComparer.Ordinal).ToArray(),
            acceptedVersions = allAccepted, reason
        }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordFactoryPublishedVersionAsync(long repositoryId, string version, string tagName,
        CancellationToken cancellationToken)
    {
        if (!SemanticReleaseVersion.TryParse(version, out _) || tagName != $"v{version}")
            throw new InvalidOperationException("Factory can only record a published MAJOR.MINOR.PATCH version with its exact v-prefixed tag.");

        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@repositoryId)", new { repositoryId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.repository_version_policy(repository_id)
            VALUES(@repositoryId) ON CONFLICT(repository_id) DO NOTHING
            """, new { repositoryId }, transaction, cancellationToken: cancellationToken));

        var previous = await connection.QuerySingleOrDefaultAsync<VersionReconciliationRow>(new CommandDefinition("""
            SELECT observed_tags AS "ObservedTags",observed_release_tags AS "ObservedReleaseTags",
              accepted_versions AS "AcceptedVersions",reason AS "Reason",reconciled_at AS "ReconciledAt"
            FROM factory.repository_version_reconciliation WHERE repository_id=@repositoryId
            ORDER BY reconciled_at DESC,id DESC LIMIT 1 FOR UPDATE
            """, new { repositoryId }, transaction, cancellationToken: cancellationToken));
        var existingVersions = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT version FROM factory.repository_published_version WHERE repository_id=@repositoryId FOR UPDATE
            """, new { repositoryId }, transaction, cancellationToken: cancellationToken))).AsList();
        var acceptedVersions = existingVersions.Append(version).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.repository_published_version(repository_id,version,source,factory_published_at)
            VALUES(@repositoryId,@version,'Factory',now())
            ON CONFLICT(repository_id,version) DO UPDATE SET
              source='Factory',factory_published_at=COALESCE(factory.repository_published_version.factory_published_at,now())
            """, new { repositoryId, version }, transaction, cancellationToken: cancellationToken));

        var observedTags = (previous?.ObservedTags ?? []).Append(tagName).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var observedReleaseTags = (previous?.ObservedReleaseTags ?? []).Append(tagName).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.repository_version_reconciliation(id,repository_id,observed_tags,
              observed_release_tags,accepted_versions,reason)
            VALUES(@id,@repositoryId,@observedTags,@observedReleaseTags,@acceptedVersions,@reason)
            """, new
        {
            id = Guid.NewGuid(), repositoryId, observedTags, observedReleaseTags, acceptedVersions,
            reason = $"Factory published {tagName} after its release promotion merged."
        }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<FactoryRelease?> CreateAsync(FactoryReleaseDraft draft, IReadOnlyList<long> githubIssueIds,
        CancellationToken cancellationToken)
    {
        var issueIds = githubIssueIds.Distinct().Order().ToArray();
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var synchronizedIssueIds = (await connection.QueryAsync<long>(new CommandDefinition("""
            SELECT id FROM github.issue
            WHERE repository_id=@repositoryId AND id=ANY(@issueIds)
            ORDER BY id FOR UPDATE
            """, new { repositoryId = draft.RepositoryId, issueIds }, transaction, cancellationToken: cancellationToken))).AsList();
        if (synchronizedIssueIds.Count != issueIds.Length)
            throw new InvalidOperationException("Every selected issue must already be synchronized in the selected repository.");

        if (issueIds.Length > 0)
        {
            // Coordinate with task claiming: a release can take over a never-started queued task, but cannot
            // silently change the base branch of a task that has ever been dispatched.
            await connection.QueryAsync<Guid>(new CommandDefinition("""
                SELECT id FROM factory.task WHERE github_issue_id=ANY(@issueIds) ORDER BY id FOR UPDATE
                """, new { issueIds }, transaction, cancellationToken: cancellationToken));
            var alreadyDispatched = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(
                  SELECT 1 FROM factory.task t WHERE t.github_issue_id=ANY(@issueIds)
                    AND (t.status <> 'Pending' OR t.started_at IS NOT NULL OR t.branch_name IS NOT NULL
                      OR t.worktree_path IS NOT NULL OR EXISTS (SELECT 1 FROM factory.run r WHERE r.task_id=t.id))
                )
                """, new { issueIds }, transaction, cancellationToken: cancellationToken));
            if (alreadyDispatched)
                throw new InvalidOperationException("This issue already has a started or previously dispatched factory task. Its captured base branch cannot be changed; associate it with a release before creating or retrying task execution.");

            var alreadyAssigned = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(
                  SELECT 1 FROM factory.release_issue ri
                  JOIN factory.release r ON r.id=ri.release_id
                  WHERE ri.github_issue_id=ANY(@issueIds)
                    AND r.status IN ('Pending','Creating','Active','Failed')
                )
                """, new { issueIds }, transaction, cancellationToken: cancellationToken));
            if (alreadyAssigned)
                throw new InvalidOperationException("An issue is already assigned to another release. Archive or cancel that release before reassigning it.");
        }

        var id = Guid.NewGuid();
        var insertedId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
            INSERT INTO factory.release(id,repository_id,name,release_number,target_branch,status,github_milestone_id,
              version_reason,version_override_reason)
            VALUES(@id,@repositoryId,@name,@releaseNumber,@targetBranch,'Pending',@githubMilestoneId,
              @versionReason,@versionOverrideReason)
            ON CONFLICT(repository_id,release_number) DO NOTHING
            RETURNING id
            """, new
        {
            id, draft.RepositoryId, name = draft.Name.Trim(), releaseNumber = draft.ReleaseNumber.Trim(),
            targetBranch = draft.TargetBranch.Trim(), draft.GitHubMilestoneId,
            draft.VersionReason, draft.VersionOverrideReason
        }, transaction, cancellationToken: cancellationToken));

        if (insertedId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (issueIds.Length > 0)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO factory.release_issue(release_id,github_issue_id)
                SELECT @id, unnest(@issueIds)
                """, new { id, issueIds }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<FactoryReleaseWorkItem?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidate AS (
              SELECT id FROM factory.release
              WHERE status='Pending' OR (status='Creating' AND updated_at < now() - interval '30 minutes')
              ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE factory.release r SET status='Creating',updated_at=now()
            FROM candidate c WHERE r.id=c.id
            RETURNING r.id,r.repository_id AS "RepositoryId",r.release_number AS "ReleaseNumber",
              r.name,r.target_branch AS "TargetBranch",r.integration_branch AS "IntegrationBranch",
              r.target_commit AS "TargetCommit"
            """;
        await using var connection = Connection();
        return await connection.QuerySingleOrDefaultAsync<FactoryReleaseWorkItem>(new CommandDefinition(sql,
            cancellationToken: cancellationToken));
    }

    public async Task<bool> RecordBranchPlanAsync(Guid id, string integrationBranch, string targetCommit,
        CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.release SET integration_branch=@integrationBranch,target_commit=@targetCommit,updated_at=now()
            WHERE id=@id AND status='Creating'
            """, new { id, integrationBranch, targetCommit }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> CompleteBranchCreationAsync(Guid id, string integrationBranch, string targetCommit,
        CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var completed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.release SET status='Active',integration_branch=@integrationBranch,target_commit=@targetCommit,
              branch_created_at=COALESCE(branch_created_at,now()),last_error=NULL,updated_at=now()
            WHERE id=@id AND status='Creating'
            """, new { id, integrationBranch, targetCommit }, transaction, cancellationToken: cancellationToken));
        if (completed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.task t SET base_branch=@integrationBranch,release_id=@id
            WHERE t.status='Pending' AND t.started_at IS NULL AND t.branch_name IS NULL AND t.worktree_path IS NULL
              AND t.release_id IS NULL AND t.github_issue_id IN (
              SELECT github_issue_id FROM factory.release_issue WHERE release_id=@id
            )
            """, new { id, integrationBranch }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task RecordBranchFailureAsync(Guid id, string error, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.release SET status='Failed',last_error=@error,updated_at=now()
            WHERE id=@id AND status='Creating'
            """, new { id, error }, cancellationToken: cancellationToken));
    }

    public async Task<bool> RetryAsync(Guid id, string? integrationBranch, string? targetBranch, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.release SET status='Pending',last_error=NULL,
              integration_branch=COALESCE(NULLIF(@integrationBranch,''),integration_branch),
              target_commit=CASE
                WHEN NULLIF(@targetBranch,'') IS NOT NULL AND NULLIF(@targetBranch,'') IS DISTINCT FROM target_branch THEN NULL
                ELSE target_commit
              END,
              target_branch=COALESCE(NULLIF(@targetBranch,''),target_branch),updated_at=now()
            WHERE id=@id AND status='Failed'
            """, new { id, integrationBranch = integrationBranch?.Trim(), targetBranch = targetBranch?.Trim() },
            cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.release SET status='Cancelled',cancelled_at=now(),last_error=NULL,updated_at=now()
            WHERE id=@id AND status IN ('Pending','Failed')
            """, new { id }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.release SET status='Archived',archived_at=now(),updated_at=now()
            WHERE id=@id AND status IN ('Active','Failed','Cancelled')
            """, new { id }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task SavePromotionAsync(Guid id, FactoryReleasePromotion promotion, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.release_promotion(
              release_id,status,pull_request_number,pull_request_url,head_commit,target_commit,
              approved_head_commit,approved_target_commit,membership_hash,approved_membership_hash,membership_issue_ids,
              ci_status,mergeability_status,last_checked_at,remaining_issues,blockers,conflicts,
              branch_cleanup_eligible,error,updated_at)
            VALUES(
              @id,@status,@pullRequestNumber,@pullRequestUrl,@headCommit,@targetCommit,
              @frozenHeadCommit,@frozenTargetCommit,@membershipHash,@frozenMembershipHash,@membershipIssueIds,
              @ciStatus,@mergeabilityStatus,@lastCheckedAt,@remainingIssues::jsonb,@blockers::jsonb,@conflicts::jsonb,
              @branchCleanupEligible,@error,now())
            ON CONFLICT(release_id) DO UPDATE SET
              status=EXCLUDED.status,
              pull_request_number=COALESCE(EXCLUDED.pull_request_number,factory.release_promotion.pull_request_number),
              pull_request_url=COALESCE(EXCLUDED.pull_request_url,factory.release_promotion.pull_request_url),
              head_commit=EXCLUDED.head_commit,target_commit=EXCLUDED.target_commit,
              approved_head_commit=COALESCE(EXCLUDED.approved_head_commit,factory.release_promotion.approved_head_commit),
              approved_target_commit=COALESCE(EXCLUDED.approved_target_commit,factory.release_promotion.approved_target_commit),
              membership_hash=EXCLUDED.membership_hash,
              approved_membership_hash=COALESCE(EXCLUDED.approved_membership_hash,factory.release_promotion.approved_membership_hash),
              membership_issue_ids=COALESCE(EXCLUDED.membership_issue_ids,factory.release_promotion.membership_issue_ids),
              ci_status=EXCLUDED.ci_status,mergeability_status=EXCLUDED.mergeability_status,
              last_checked_at=EXCLUDED.last_checked_at,remaining_issues=EXCLUDED.remaining_issues,
              blockers=EXCLUDED.blockers,conflicts=EXCLUDED.conflicts,
              branch_cleanup_eligible=EXCLUDED.branch_cleanup_eligible,error=EXCLUDED.error,updated_at=now()
            """, new
        {
            id, promotion.Status, pullRequestNumber = promotion.PullRequestNumber,
            pullRequestUrl = promotion.PullRequestUrl, headCommit = promotion.HeadCommit,
            targetCommit = promotion.TargetCommit, frozenHeadCommit = promotion.FrozenHeadCommit,
            frozenTargetCommit = promotion.FrozenTargetCommit, membershipHash = promotion.MembershipHash,
            frozenMembershipHash = promotion.FrozenMembershipHash,
            membershipIssueIds = promotion.MembershipIssueIds.Count == 0 ? null : promotion.MembershipIssueIds.ToArray(),
            promotion.CiStatus, promotion.MergeabilityStatus, lastCheckedAt = promotion.LastCheckedAt,
            remainingIssues = JsonSerializer.Serialize(promotion.RemainingIssues),
            blockers = JsonSerializer.Serialize(promotion.Blockers), conflicts = JsonSerializer.Serialize(promotion.Conflicts),
            promotion.BranchCleanupEligible, promotion.Error
        }, cancellationToken: cancellationToken));
    }

    public async Task SaveVersionPublicationAsync(Guid id, FactoryReleaseVersionPublication publication,
        CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.release_promotion SET
              version_publication_status=@status,
              version_publication_repository_id=@repositoryId,
              version_publication_repository=@repository,
              version_planned_version=@plannedVersion,
              version_tag_name=@tagName,
              version_target_branch_commit=@targetBranchCommit,
              github_release_id=@githubReleaseId,
              github_release_url=@githubReleaseUrl,
              version_tag_recorded_at=@tagRecordedAt,
              version_published_at=@publishedAt,
              version_publication_started_at=@startedAt,
              version_publication_completed_at=@completedAt,
              version_publication_last_attempt_at=@lastAttemptAt,
              version_publication_attempt_count=@attemptCount,
              version_publication_last_error=@lastError,
              updated_at=now()
            WHERE release_id=@id
            """, new
        {
            id, status = publication.Status, repositoryId = publication.RepositoryId,
            repository = publication.Repository, plannedVersion = publication.PlannedVersion,
            tagName = publication.TagName, targetBranchCommit = publication.TargetBranchCommit,
            githubReleaseId = publication.GitHubReleaseId, githubReleaseUrl = publication.GitHubReleaseUrl,
            tagRecordedAt = publication.TagRecordedAt, publishedAt = publication.PublishedAt,
            startedAt = publication.StartedAt, completedAt = publication.CompletedAt,
            lastAttemptAt = publication.LastAttemptAt, attemptCount = publication.AttemptCount,
            lastError = publication.LastError
        }, cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyList<FactoryRelease>> AddIssuesAsync(NpgsqlConnection connection,
        IReadOnlyList<ReleaseRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(row => row.Id).ToArray();
        var issues = await connection.QueryAsync<FactoryReleaseIssueRow>(new CommandDefinition("""
            SELECT ri.release_id AS "ReleaseId",i.id AS "GitHubIssueId",i.issue_number AS "IssueNumber",
              i.title,i.state,
              EXISTS(SELECT 1 FROM github.issue_label l WHERE l.issue_id=i.id AND lower(l.name)='factory:ready') AS "Eligible",
              linked_task.status AS "TaskStatus",linked_task.id AS "TaskId",linked_task.base_branch AS "TaskBaseBranch",
              linked_task.release_id AS "TaskReleaseId",linked_task.ci_status AS "CiStatus",
              linked_task.pull_request_number AS "PullRequestNumber",linked_task.pull_request_url AS "PullRequestUrl"
            FROM factory.release_issue ri JOIN github.issue i ON i.id=ri.github_issue_id
            LEFT JOIN LATERAL (
              SELECT t.id,t.status,t.base_branch,t.release_id,ci.overall_status AS ci_status,
                publication.pull_request_number,publication.pull_request_url
              FROM factory.task t
              LEFT JOIN factory.task_ci_status ci ON ci.task_id=t.id
              LEFT JOIN LATERAL (
                SELECT p.pull_request_number,p.pull_request_url FROM factory.publication p
                WHERE p.task_id=t.id AND p.pull_request_url IS NOT NULL ORDER BY p.requested_at DESC LIMIT 1
              ) publication ON true
              WHERE t.github_issue_id=i.id ORDER BY t.created_at DESC LIMIT 1
            ) linked_task ON true
            WHERE ri.release_id=ANY(@ids)
            ORDER BY i.issue_number
        """, new { ids }, cancellationToken: cancellationToken));
        var issuesByRelease = issues.GroupBy(issue => issue.ReleaseId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<FactoryReleaseIssue>)group.Select(issue => issue.Issue).ToArray());
        var promotions = (await connection.QueryAsync<PromotionRow>(new CommandDefinition("""
            SELECT release_id AS "ReleaseId",status AS "Status",pull_request_number AS "PullRequestNumber",
              pull_request_url AS "PullRequestUrl",head_commit AS "HeadCommit",target_commit AS "TargetCommit",
              approved_head_commit AS "FrozenHeadCommit",approved_target_commit AS "FrozenTargetCommit",
              membership_hash AS "MembershipHash",approved_membership_hash AS "FrozenMembershipHash",
              membership_issue_ids AS "MembershipIssueIds",ci_status AS "CiStatus",mergeability_status AS "MergeabilityStatus",
              last_checked_at AS "LastCheckedAt",remaining_issues::text AS "RemainingIssuesJson",
              blockers::text AS "BlockersJson",conflicts::text AS "ConflictsJson",
              branch_cleanup_eligible AS "BranchCleanupEligible",error AS "Error",
              version_publication_status AS "VersionPublicationStatus",
              version_publication_repository_id AS "VersionPublicationRepositoryId",
              version_publication_repository AS "VersionPublicationRepository",
              version_planned_version AS "VersionPlannedVersion",version_tag_name AS "VersionTagName",
              version_target_branch_commit AS "VersionTargetBranchCommit",github_release_id AS "GitHubReleaseId",
              github_release_url AS "GitHubReleaseUrl",version_tag_recorded_at AS "VersionTagRecordedAt",
              version_published_at AS "VersionPublishedAt",version_publication_started_at AS "VersionStartedAt",
              version_publication_completed_at AS "VersionCompletedAt",version_publication_last_attempt_at AS "VersionLastAttemptAt",
              version_publication_attempt_count AS "VersionAttemptCount",version_publication_last_error AS "VersionLastError"
            FROM factory.release_promotion WHERE release_id=ANY(@ids)
            """, new { ids }, cancellationToken: cancellationToken))).ToDictionary(row => row.ReleaseId);
        return rows.Select(row => row.ToModel(issuesByRelease.GetValueOrDefault(row.Id) ?? []) with
        {
            Promotion = promotions.TryGetValue(row.Id, out var promotion) ? promotion.ToModel() : null
        }).ToArray();
    }

    private const string ReleaseSelect = """
        SELECT fr.id,fr.repository_id AS "RepositoryId",gr.owner || '/' || gr.name AS "Repository",
          fr.name,fr.release_number AS "ReleaseNumber",fr.integration_branch AS "IntegrationBranch",
          fr.target_branch AS "TargetBranch",fr.target_commit AS "TargetCommit",fr.status,
          fr.created_at AS "CreatedAt",fr.updated_at AS "UpdatedAt",fr.branch_created_at AS "BranchCreatedAt",
          fr.github_milestone_id AS "GitHubMilestoneId",fr.last_error AS "LastError",
          fr.version_reason AS "VersionReason",fr.version_override_reason AS "VersionOverrideReason"
        FROM factory.release fr JOIN github.repository gr ON gr.id=fr.repository_id
        """;

    private sealed class ReleaseRow
    {
        public Guid Id { get; init; }
        public long RepositoryId { get; init; }
        public string Repository { get; init; } = "";
        public string Name { get; init; } = "";
        public string ReleaseNumber { get; init; } = "";
        public string? IntegrationBranch { get; init; }
        public string TargetBranch { get; init; } = "";
        public string? TargetCommit { get; init; }
        public string Status { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public DateTime? BranchCreatedAt { get; init; }
        public long? GitHubMilestoneId { get; init; }
        public string? LastError { get; init; }
        public string? VersionReason { get; init; }
        public string? VersionOverrideReason { get; init; }

        public FactoryRelease ToModel(IReadOnlyList<FactoryReleaseIssue> issues) => new(Id, RepositoryId, Repository,
            Name, ReleaseNumber, IntegrationBranch, TargetBranch, TargetCommit, Enum.Parse<FactoryReleaseStatus>(Status),
            Offset(CreatedAt), Offset(UpdatedAt), BranchCreatedAt is null ? null : Offset(BranchCreatedAt.Value),
            GitHubMilestoneId, LastError, issues, VersionReason, VersionOverrideReason);

        private static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed record FactoryReleaseIssueRow(Guid ReleaseId, long GitHubIssueId, int IssueNumber, string Title,
        string State, bool Eligible, string? TaskStatus, Guid? TaskId, string? TaskBaseBranch, Guid? TaskReleaseId,
        string? CiStatus, int? PullRequestNumber, string? PullRequestUrl)
    {
        public FactoryReleaseIssue Issue => new(GitHubIssueId, IssueNumber, Title, State, Eligible, TaskStatus,
            TaskId, TaskBaseBranch, TaskReleaseId, CiStatus, PullRequestNumber, PullRequestUrl);
    }

    private sealed class PromotionRow
    {
        public Guid ReleaseId { get; init; }
        public string Status { get; init; } = "";
        public int? PullRequestNumber { get; init; }
        public string? PullRequestUrl { get; init; }
        public string? HeadCommit { get; init; }
        public string? TargetCommit { get; init; }
        public string? FrozenHeadCommit { get; init; }
        public string? FrozenTargetCommit { get; init; }
        public string? MembershipHash { get; init; }
        public string? FrozenMembershipHash { get; init; }
        public long[]? MembershipIssueIds { get; init; }
        public string CiStatus { get; init; } = "NotChecked";
        public string MergeabilityStatus { get; init; } = "NotChecked";
        public DateTime? LastCheckedAt { get; init; }
        public string RemainingIssuesJson { get; init; } = "[]";
        public string BlockersJson { get; init; } = "[]";
        public string ConflictsJson { get; init; } = "[]";
        public bool BranchCleanupEligible { get; init; }
        public string? Error { get; init; }
        public string VersionPublicationStatus { get; init; } = "NotStarted";
        public long? VersionPublicationRepositoryId { get; init; }
        public string? VersionPublicationRepository { get; init; }
        public string? VersionPlannedVersion { get; init; }
        public string? VersionTagName { get; init; }
        public string? VersionTargetBranchCommit { get; init; }
        public long? GitHubReleaseId { get; init; }
        public string? GitHubReleaseUrl { get; init; }
        public DateTime? VersionTagRecordedAt { get; init; }
        public DateTime? VersionPublishedAt { get; init; }
        public DateTime? VersionStartedAt { get; init; }
        public DateTime? VersionCompletedAt { get; init; }
        public DateTime? VersionLastAttemptAt { get; init; }
        public int VersionAttemptCount { get; init; }
        public string? VersionLastError { get; init; }

        public FactoryReleasePromotion ToModel() => new(Status, PullRequestNumber, PullRequestUrl, HeadCommit, TargetCommit,
            FrozenHeadCommit, FrozenTargetCommit, MembershipHash, FrozenMembershipHash, MembershipIssueIds ?? [], CiStatus,
            MergeabilityStatus, LastCheckedAt is null ? null : Utc(LastCheckedAt.Value),
            JsonSerializer.Deserialize<string[]>(RemainingIssuesJson) ?? [], JsonSerializer.Deserialize<string[]>(BlockersJson) ?? [],
            JsonSerializer.Deserialize<string[]>(ConflictsJson) ?? [], BranchCleanupEligible, Error,
            VersionPublicationRepositoryId is null ? null : new FactoryReleaseVersionPublication(
                VersionPublicationStatus, VersionPublicationRepositoryId.Value, VersionPublicationRepository ?? "",
                VersionPlannedVersion ?? "", VersionTagName, VersionTargetBranchCommit, GitHubReleaseId,
                GitHubReleaseUrl, VersionTagRecordedAt is null ? null : Utc(VersionTagRecordedAt.Value),
                VersionPublishedAt is null ? null : Utc(VersionPublishedAt.Value),
                VersionStartedAt is null ? null : Utc(VersionStartedAt.Value),
                VersionCompletedAt is null ? null : Utc(VersionCompletedAt.Value),
                VersionLastAttemptAt is null ? null : Utc(VersionLastAttemptAt.Value), VersionAttemptCount,
                VersionLastError));

        private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed class VersionPolicyRow
    {
        public string VersionFormat { get; init; } = "";
        public string TagPrefix { get; init; } = "";
        public string BreakingChangeDefinition { get; init; } = "";
    }

    private sealed class VersionReconciliationRow
    {
        public string[] ObservedTags { get; init; } = [];
        public string[] ObservedReleaseTags { get; init; } = [];
        public string[] AcceptedVersions { get; init; } = [];
        public string Reason { get; init; } = "";
        public DateTime ReconciledAt { get; init; }

        public RepositoryVersionReconciliation ToModel() => new(ObservedTags, ObservedReleaseTags, AcceptedVersions,
            Reason, new DateTimeOffset(DateTime.SpecifyKind(ReconciledAt, DateTimeKind.Utc)));
    }
}

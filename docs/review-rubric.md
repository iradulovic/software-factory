# Independent review rubric

The reviewer records each finding in `.factory/review.json`. The orchestrator validates the structured fields and applies the policy below; it never decides from the finding description or overall score.

| Severity | Required structured detail | Orchestrator disposition |
|---|---|---|
| High | No impact flag. A high finding cannot be marked advisory. | Always requires a fix. |
| Medium | `mediumImpact` is `acceptance-criterion`, `user-workflow`, or `advisory`, with a short `rationale`. | Requires a fix for an acceptance-criterion or real user-workflow impact; otherwise advisory. |
| Low | No impact flag. | Advisory. |

The optional overall `score` is an integer from 1 to 5 and must be accompanied by a short `scoreRationale`. It summarizes review quality for the operator only. It cannot waive a required fix or cause a fix by itself. Existing review results that omit both score fields remain valid.

Each validated verdict, policy reason, score, and finding is persisted. Required findings are sent to the original implementing agent in the bounded `AgentReviewFix` loop, then independently validated and reviewed again. If required findings remain after `MaxReviewFixAttempts`, the task moves to `NeedsHuman` and is not published. A reviewer-requested `blocked`, `needs-human`, or `needsHuman: true` disposition also moves to `NeedsHuman`. Missing, malformed, failed, and quota-interrupted review results remain review invocation failures governed by `MaxReviewAttempts` and are not recorded as passing scores.

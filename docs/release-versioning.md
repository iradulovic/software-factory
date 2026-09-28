# Release versioning

Factory assigns a separate version sequence to each managed repository. A release is **planned** while its integration branch is being prepared; planning a release does not publish a version. A version becomes **published** only when it is present in the repository's confirmed published-version history, which is reconciled with Git tags and published GitHub Releases before Factory suggests another version. The promotion and publication workflow remains responsible for recording a newly published version.

New planned versions use `MAJOR.MINOR.PATCH` and publish as a `vMAJOR.MINOR.PATCH` Git tag. For a repository with a published version, operators choose the reason for the change: bug fixes increase PATCH, new features increase MINOR, and breaking changes increase MAJOR. When no version has been published, the operator enters an explicit starting version. Versions are never inferred from issue labels, commits, or AI output.

A change is breaking when an operator must change a documented workflow, a documented HTTP API contract, or supported configuration to keep a managed application working. The operator can override a suggested version when it is above the latest published version and unique within that repository; the override requires an explanation.

Factory keeps each repository's confirmed published versions and its reconciliation of Git tag and GitHub Release history in PostgreSQL. A mismatch, invalid version tag, or unexplained history change blocks suggestions until an operator reviews the observed versions and records the decision. Reconciliation never deletes a version already recorded as published.

Existing planned release identifiers are preserved and displayed as they were saved. For example, a pre-policy value such as `2.4` remains readable as a legacy planned identifier; Factory does not rewrite it or migration 039. New releases must use three numeric components. Two repositories may independently plan and publish the same version.

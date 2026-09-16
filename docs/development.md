# Development notes

Migrations are ordered SQL files under `database/migrations`. Never edit an applied migration; add the next numbered file. Each host applies pending migrations at startup.

Expected operational failures are persisted as task and run failures. Agent stdout and stderr are stored for this bootstrap; production hardening should move large output to files/object storage while retaining paths in PostgreSQL.

Repository validation commands are read from `.factory/config.json` in the target repository, falling back to `dotnet build` and `dotnet test`. Command parsing is intentionally simple in V1: executable followed by space-separated arguments, with no shell expansion.

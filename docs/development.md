# Development notes

Migrations are ordered SQL files under `database/migrations`. Never edit an applied migration; add the next numbered file. Each host applies pending migrations at startup.

Expected operational failures are persisted as task and run failures. Agent stdout and stderr are stored for this bootstrap; production hardening should move large output to files/object storage while retaining paths in PostgreSQL.

Repository validation commands are read from `.factory/config.json` in the target repository, falling back to `dotnet build` and `dotnet test`. Command parsing is intentionally simple in V1: executable followed by space-separated arguments, with no shell expansion.

## Docker Desktop on Windows

Docker Desktop may be installed for the current user without adding its CLI to the `PATH` inherited by terminals or coding agents. Resolve the CLI once and invoke it explicitly:

```powershell
$dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
$dockerCli = if ($dockerCommand) {
    $dockerCommand.Source
} else {
    Join-Path $env:LOCALAPPDATA "Programs\DockerDesktop\resources\bin\docker.exe"
}

if (-not (Test-Path -LiteralPath $dockerCli)) {
    throw "Docker CLI not found. Start Docker Desktop and verify its installation."
}

& $dockerCli version
& $dockerCli compose ps
& $dockerCli compose up -d postgres
```

The current Windows installation uses the user-local fallback above. Docker Desktop may need to be started from its Start Menu shortcut before the daemon is available. A successful `docker version` must show both Client and Server sections; a client-only result does not prove daemon access.

To execute the PostgreSQL-backed tests against the Compose service, use a disposable database:

```powershell
& $dockerCli exec software-factory-postgres-1 createdb -U factory software_factory_tests
$env:FACTORY_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Database=software_factory_tests;Username=factory;Password=factory"
dotnet test tests/Factory.IntegrationTests/Factory.IntegrationTests.csproj
```

`createdb` reports an error when the database already exists; that is safe to ignore for subsequent test runs. The tests isolate their records and clean them up.

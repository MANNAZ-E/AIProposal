# Saga — CLAUDE.md

Saga is an ASP.NET Core Blazor Server app (.NET 10) that generates and reviews
proposal documents. Solution: `Saga.slnx` (`src/Saga.Core`, `src/Saga.Infrastructure`,
`src/Saga.Web`, `tests/Saga.Tests`).

## Running the app

Prereqs: .NET 10 SDK, SQL Server LocalDB (`sqllocaldb info` should list `MSSQLLocalDB`).

```
cd src/Saga.Web
dotnet run --launch-profile http --no-build   # after an initial `dotnet build`
```

- Listens on `http://localhost:5033` (see `src/Saga.Web/Properties/launchSettings.json`;
  an `https` profile also exists on `7070`/`5033`).
- EF Core migrations apply automatically on startup in Development
  (`Program.cs`, `db.Database.MigrateAsync()`), against the LocalDB connection string
  in `appsettings.Development.json`.
- Dev auth auto-signs in as `elv@mannaz.com` (`Auth:DevAutoSignIn: true` in
  `appsettings.Development.json`) — no real Entra ID login needed locally.
- Azure OpenAI / Content Understanding endpoints are blank in dev, so the app falls
  back to `FakeAiService` / `FakeDocumentExtractor` automatically — no Azure needed
  to run and click through the app locally. Set `ContentUnderstanding:Endpoint` to a
  Foundry resource endpoint (and `az login`) to parse uploads for real via the
  `prebuilt-layout` analyzer.
- `dotnet run` does not open a browser by itself in this environment; navigate to
  `http://localhost:5033` manually (or `Start-Process http://localhost:5033` in
  PowerShell) once the log shows `Now listening on: http://localhost:5033`.
- Run in the background (e.g. `run_in_background` in Claude Code) since it's a
  long-lived server; stop it by killing the process (Ctrl+C interactively, or kill
  the backgrounded shell).

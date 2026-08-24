---
name: run-saga
description: Build, launch, and stop the Saga Blazor Server app locally on http://localhost:5033. Use whenever asked to run, start, restart, or screenshot the app, or to verify a change works in the real app rather than only in tests.
---

# Run Saga locally

Saga is an ASP.NET Core Blazor Server app (.NET 10). No real Entra ID login is
needed in Development, but AI calls and uploads now hit real Azure and cost money
— see "What costs money" below.

## Prereqs (check once, only if launch fails)

- `dotnet --version` → 10.x
- `sqllocaldb info` → lists `MSSQLLocalDB` (EF Core migrations apply on startup
  in Development, against the LocalDB connection string in
  `src/Saga.Web/appsettings.Development.json`)

## Launch

Run from the repo root. Always start with a build so compile errors surface as
build output instead of getting buried in server logs:

```
dotnet build Saga.slnx
```

Then start the server **in the background** (it is long-lived — never run it in
the foreground, it will block until timeout):

```
cd src/Saga.Web && dotnet run --launch-profile http --no-build
```

Wait for `Now listening on: http://localhost:5033` in the background output
before doing anything else. Startup also runs `db.Database.MigrateAsync()`, so
the first run after a new migration takes noticeably longer.

Ports: `http` profile → `http://localhost:5033`. An `https` profile also exists
(`https://localhost:7070` plus `5033`); prefer `http` locally to avoid dev-cert
prompts.

## Open it

`dotnet run` does not open a browser in this environment. Either tell the user to
visit `http://localhost:5033`, or open it yourself:

- PowerShell: `Start-Process http://localhost:5033`
- To click through or screenshot: use the `claude-in-chrome` skill and navigate a
  new tab to `http://localhost:5033`.

## What still works without Azure

- **Auth** — `Auth:DevAutoSignIn: true` auto-signs in as `elv@mannaz.com`. No
  login screen; you land straight in the app.
## What costs money

- **AI + document extraction no longer run offline.** `appsettings.Development.json`
  points `AzureOpenAI:Endpoint` and `ContentUnderstanding:Endpoint` at the
  `MannazAIProposal` Foundry resource with both `Ai:UseFake*` flags false, so
  generations call `gpt-5.6-luna` and uploads call `prebuilt-layout` for real.
  Auth is the developer's `az login` (no key), so `az account show` must succeed
  or every call fails with a 401.
- To exercise the UI without spending: set `Ai:UseFakeAi: true` (and
  `Ai:UseFakeExtractor: true` for uploads) and restart — the flags are read once
  at startup and force `FakeAiService` / `FakeDocumentExtractor` even with the
  endpoints configured. Their output is canned, so it is *not* a check of real
  prompt/model behaviour — say so when reporting results.

## Stop it

Kill the background shell running `dotnet run` (Ctrl+C if interactive). If port
5033 is still held afterwards:

```
Get-NetTCPConnection -LocalPort 5033 -State Listen |
  ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }
```

## Restarting after a code change

Blazor Server has no hot reload under plain `dotnet run` here: stop the process,
`dotnet build Saga.slnx`, and start it again. A build that fails while the old
server is still running usually means the DLL is locked — stop the server first.

## Troubleshooting

- **Port already in use** — an earlier run is still alive; stop it as above.
- **Migration / LocalDB errors on startup** — `sqllocaldb start MSSQLLocalDB`,
  then retry.
- **Blank page or a stuck "Reconnecting" overlay** — the server died after the
  browser connected; read the background shell output for the exception.

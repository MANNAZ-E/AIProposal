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
- **Dev now calls real Azure and costs money on every generation and upload.**
  `AzureOpenAI:Endpoint` and `ContentUnderstanding:Endpoint` both point at the
  `MannazAIProposal` Foundry resource and both `Ai:UseFake*` flags are false, so
  LLM calls hit `gpt-5.6-luna` and uploads are parsed by the `prebuilt-layout`
  analyzer. Auth is `az login` locally (no key), so `az account show` must work.
- The offline stand-ins `FakeAiService` / `FakeDocumentExtractor` are used when
  `Ai:UseFakeAi` / `Ai:UseFakeExtractor` are true **or** the matching endpoint is
  blank. Setting `Ai:UseFakeAi: true` forces the fake LLM even with the real
  endpoint configured — that is the switch for UI testing without spending
  tokens, and `Ai:UseFakeExtractor: true` does the same for uploads. Both flags
  are read once at startup, so changing one needs a restart.
- `dotnet run` does not open a browser by itself in this environment; navigate to
  `http://localhost:5033` manually (or `Start-Process http://localhost:5033` in
  PowerShell) once the log shows `Now listening on: http://localhost:5033`.
- Run in the background (e.g. `run_in_background` in Claude Code) since it's a
  long-lived server; stop it by killing the process (Ctrl+C interactively, or kill
  the backgrounded shell).

## AI usage and cost tracking

Every paid call — LLM prompt or Content Understanding extraction — writes one `AiUsageRecord`
row (`AiUsage` table), including the full request and response text so a call can be
reconstructed later.

- Capture happens in decorators, **not** in the services: `UsageTrackingAiService` wraps
  `IAiService` and `UsageTrackingTextExtractor` wraps the billed extractor, both registered in
  `Program.cs`. Services attribute a call by attaching an `AiCallContext`
  (`src/Saga.Core/Abstractions/AiCallContext.cs`) to the `AiRequest` — never by writing rows
  themselves. A request with no context passes through unmetered.
- One row per *call*; an `OperationId` groups the calls of one user-visible operation (content
  generation runs one call per unit, requirements extraction one per chunk). Rejecting a
  generation marks every row of its operation.
- Both model tiers (`AiModelTier.Strong` / `.Light`) currently resolve to the same deployment,
  `gpt-5.6-luna`; `gpt-5.6-terra` stays deployed and priced, so moving the Strong tier back is
  the `AzureOpenAI:StrongDeployment` setting alone — no call site passes a model name.
- Rates live in the `Pricing` config section, keyed by deployment name, **in USD** (what Azure
  publishes); `Pricing:UsdToDkk` converts for display only. Cost is frozen on the row at write
  time. `PricingService` returns 0 for an unpriced model rather than throwing — metering must
  never break a generation.
- Input tokens the provider served from its cache are billed at `CachedInputPer1M`, a fraction of
  the input rate, and only the uncached remainder at `InputPer1M` — it matters because the system
  prompt and working context repeat across every call of a run. Omitting the key falls back to the
  full input rate. A *succeeded* call reporting zero tokens means the provider sent no usage data,
  not that it was free, so `UsageTrackingAiService` logs a warning rather than banking the zero.
- Visible per proposal on the workspace **Usage** tab (`UsageSection.razor`) and across all
  proposals on `/admin`. Dev prices for `fake-model` are set in `appsettings.Development.json`
  so local numbers are non-zero.

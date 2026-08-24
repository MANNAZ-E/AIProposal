# Model rename to GPT-5.6, real prices, and an explicit fake-services flag

> **Status: implemented 2026-08-24.** Build clean, 102 tests pass (was 82 before this change).
> Kept as the record of what changed and why.
>
> Settled while implementing, both without needing a code change:
> `ChatTokenUsage.OutputTokenCount` already includes reasoning tokens (the SDK documents it as
> "the sum of those reasoning tokens and conventional, displayed output tokens"), and
> `ChatCompletionOptions.StreamOptions` takes an internal type, so the SDK sends the streaming
> usage opt-in itself.
>
> Added beyond the plan, because the browser could not be driven reliably to verify by clicking:
> - `StandInSelection` (`src/Saga.Infrastructure/Ai/StandInSelection.cs`) extracts the
>   fake-vs-real decision out of `Program.cs` so it is unit-testable, covered by
>   `StandInSelectionTests` — including the case a blank-endpoint check alone gets wrong
>   (endpoint configured, flag on).
> - `PricingConfigurationTests` loads the **shipped** appsettings files and asserts every
>   configured deployment name has a working price key, that cached input is cheaper than
>   uncached, and that production does not ship the stand-ins switched on. Verified to fail
>   when a deployment is renamed without its price key.
>
> Not verified: no call has been made against a real Foundry endpoint, so the cached-token
> count arriving non-zero in practice is still unconfirmed. See `TODO.md` item 7.

## Context

Two questions, one change set.

**Where prices are entered.** There is no UI for it — prices are configuration only.
`PricingService` (`src/Saga.Infrastructure/Ai/PricingService.cs`) reads
`Pricing:Models:<deployment>:InputPer1M` / `:OutputPer1M` from `IConfiguration` on every call,
in USD. The key must equal the **deployment name** that `AzureOpenAiService` reports back in
`AiStreamEvent.Completed` (`AzureOpenAiService.cs:70`) — there is no tier-based lookup. Three
places to enter them:

| Where | File / setting |
|---|---|
| Committed defaults | `src/Saga.Web/appsettings.json` → `Pricing:Models` (all zeros today) |
| Local dev | `src/Saga.Web/appsettings.Development.json` → `Pricing:Models` (only `fake-model` today) |
| Production | App Service app settings, `:` → `__`, e.g. `Pricing__Models__gpt-5.6-terra__InputPer1M` |

Cost is computed and **frozen onto the row** at write time (`UsageTrackingAiService.cs:88`), so
changing a rate never rewrites history. A model with no rate logs at zero with a one-time warning.

**What changes now.** The app is still configured for `gpt-5.4` / `gpt-5.4-mini`, which were
never provisioned. Switch to `gpt-5.6-terra` (Strong) and `gpt-5.6-luna` (Light), enter the real
USD list prices including the cached-input rate, and make the offline stand-in AI selectable by an
explicit flag instead of only by blanking `AzureOpenAI:Endpoint` — so UI testing stays possible
with a real endpoint configured.

Out of scope by decision: invoice/Cost-Management reconciliation, currency handling changes
(`Pricing:UsdToDkk` stays 6.9), and long-context / cache-write rate tiers.

## 1. Rename the deployments

`gpt-5.4` → `gpt-5.6-terra` (Strong: analysis, generation, chat, review)
`gpt-5.4-mini` → `gpt-5.6-luna` (Light: requirements extraction, condensation)

Confirmed: the Azure Foundry deployments will be created under exactly these names.

The tier→deployment mapping (`AzureOpenAiService.cs:37`) and every `AiModelTier` call site stay
exactly as they are — no service passes a model name, only a tier. Rename in:

- `src/Saga.Web/appsettings.json` — `AzureOpenAI:StrongDeployment` / `LightDeployment`, and the
  two `Pricing:Models` keys.
- `src/Saga.Web/appsettings.Development.json` — same two deployment keys.
- `src/Saga.Infrastructure/Ai/AzureOpenAiService.cs:30-31` — the `?? "gpt-5.4"` fallbacks.
- `src/Saga.Core/Abstractions/IAiService.cs:3` — the `AiModelTier` doc comment ("Strong (GPT 5.4)…").
- `tests/Saga.Tests/PricingTests.cs:18-41` — the config keys and expected values.
- `docs/azure-provisioning.md` — step 4.2, the step 7 settings table, and the `$…ModelVersion` note.
- `scripts/provision-azure.ps1:81-82` (`$StrongModelName`/`$LightModelName`/deployments) and the
  availability-check comments at :55 and :84.
- `TODO.md:17-18`.

## 2. Enter the prices, and bill cached input at the cached rate

Rates supplied by Emil, USD per 1M tokens, **short-context tier**:

| Model | Input | Cached input | Output |
|---|---|---|---|
| GPT-5.6 Terra (Strong) | $4.40 | $0.44 | $26.40 |
| GPT-5.6 Luna (Light) | $1.10 | $0.11 | $6.60 |

Short-context rates are the right ones: the working context is capped at
`AzureOpenAI:ContextTokenBudget` = 100 000 tokens (`WorkingContextService.cs:21`). Long-context
and cache-write columns are deliberately not modelled.

`src/Saga.Web/appsettings.json` — replace the two zeroed entries:

```json
"Models": {
  "gpt-5.6-terra": { "InputPer1M": 4.40, "CachedInputPer1M": 0.44, "OutputPer1M": 26.40 },
  "gpt-5.6-luna":  { "InputPer1M": 1.10, "CachedInputPer1M": 0.11, "OutputPer1M": 6.60 }
}
```

`appsettings.Development.json` — keep the existing `fake-model` entry (1.25 / 10.0) and add the
same two real entries, so local numbers are right whenever dev points at a real endpoint.

### `CachedInputPer1M` support in `PricingService`

Both inputs already exist: the rate is above, and the count is captured at
`AzureOpenAiService.cs:66` (`Usage.InputTokenDetails?.CachedTokenCount`) and stored as
`AiUsageRecord.CachedInputTokens`. Only the arithmetic is missing — today all input bills at the
full rate, a 10× overcharge on cached tokens, which matter because the system prompt and working
context repeat across every call in a run.

- `EstimateLlmUsd(string model, int inputTokens, int outputTokens, int cachedInputTokens = 0)` —
  the default keeps existing callers and tests compiling.
- Read `Pricing:Models:{model}:CachedInputPer1M`; **if unset (0), fall back to the full input
  rate**, so a config without the key behaves exactly as today.
- `var billable = Math.Max(0, inputTokens - cachedInputTokens);`
  `cost = (billable * input + cached * cachedRate + outputTokens * output) / 1_000_000m`
- `WarnOnce` trigger stays `input == 0 && output == 0` — a missing cached rate is not a warning.
- Update the single real caller, `UsageTrackingAiService.cs:88`, to pass `c.CachedPromptTokens`.

Add two `PricingTests` cases: cached tokens priced at the cached rate, and the fallback to the
full input rate when `CachedInputPer1M` is absent.

## 3. Make missing usage data visible

The one way the cached-rate work silently fails: `AzureOpenAiService.cs:53` calls
`CompleteChatStreamingAsync` without explicitly opting into streamed usage. If `update.Usage`
is never populated, every real call records 0 tokens and $0.00 — and a zero-cost row looks like a
cheap call, not like broken instrumentation. Cached tokens would read 0 too, so everything would
quietly bill at the full input rate.

- In `UsageTrackingAiService`, when a call completes successfully with `InputTokens == 0 &&
  OutputTokens == 0`, log a warning naming the model. The fake reports non-zero tokens, so this
  cannot fire in dev and will not be noise.
- While in `AzureOpenAiService`, check whether `ChatCompletionOptions` in Azure.AI.OpenAI 2.1.0
  exposes a stream-options / include-usage setting, and set it if so. If the SDK already requests
  usage by default, leave the call as is and note that in a comment.
- Also confirm whether `Usage.OutputTokenDetails?.ReasoningTokenCount` is already included in
  `OutputTokenCount` or additive. Reasoning tokens bill as output on GPT-5.x; if additive, add
  them to `completionTokens`. If unclear from the SDK, leave the code alone and record the
  question in `TODO.md` for the first real call to settle.

## 4. Explicit fake-services flags

Today the only switch is "is the endpoint blank?" (`Program.cs:74` and `:93`). Add two independent
booleans under a new `Ai` section, OR-ed with the existing endpoint check so nothing regresses:

```csharp
// Program.cs — IAiService factory
IAiService inner = config.GetValue("Ai:UseFakeAi", false)
                   || string.IsNullOrEmpty(config["AzureOpenAI:Endpoint"])
    ? new FakeAiService()
    : new AzureOpenAiService(config);

// Program.cs — IDocumentTextExtractor factory
IDocumentTextExtractor billed = config.GetValue("Ai:UseFakeExtractor", false)
                                || string.IsNullOrEmpty(config["ContentUnderstanding:Endpoint"])
    ? new FakeDocumentExtractor()
    : new ContentUnderstandingExtractor(config);
```

Config:
- `appsettings.json` → `"Ai": { "UseFakeAi": false, "UseFakeExtractor": false }`.
- `appsettings.Development.json` → `"Ai": { "UseFakeAi": true, "UseFakeExtractor": false }`.
  `UseFakeAi: true` matches today's behaviour (blank endpoint) but *keeps* the fake once a real
  endpoint is pasted in — that is the UI-testing escape hatch. `UseFakeExtractor: false` preserves
  today's real Content Understanding parsing in dev; flip it to `true` to stop uploads billing.

Requires a restart to take effect (the factories run once, at DI registration).

Update the `FakeAiService` class comment (`FakeAiService.cs:7-8`), which currently says the
stand-in is used "when AzureOpenAI:Endpoint is not configured", and the `Program.cs:92` comment.

## 5. Documentation

- `CLAUDE.md` — the "Running the app" bullet about the fake fallback: mention `Ai:UseFakeAi` /
  `Ai:UseFakeExtractor`. Add `CachedInputPer1M` to the "AI usage and cost tracking" rates bullet.
- `docs/azure-provisioning.md` — step 7 table: new model names, plus the two
  `Pricing__Models__<name>__CachedInputPer1M` rows and the `Ai__UseFakeAi` /
  `Ai__UseFakeExtractor` rows (both `false` in production).
- `scripts/provision-azure.ps1:322-325` — **stale setting names to fix**: the script writes
  `AzureOpenAI__StrongPrice__InputPer1M` etc., which `PricingService` never reads, so provisioning
  as written would leave production unpriced. Replace with `Pricing__Models__gpt-5.6-terra__*` /
  `Pricing__Models__gpt-5.6-luna__*` (input, cached, output),
  `Pricing__ContentUnderstanding__prebuilt-layout__Per1000Pages`, and `Pricing__UsdToDkk`; rename
  the `$…Price…` variables at :90-95 to match and pre-fill them with the rates above.
- Add a comment beside the `Pricing:Models` block recording where the rates came from and the date
  checked, so a stale number is traceable later.
- `TODO.md` item 7 — narrow it to: verify on the first real call that token counts and
  `CachedInputTokens` arrive non-zero, and re-check the rates against current Azure pricing.
  The rates themselves are now entered.
- Refresh `docs/plans/gpt-5.6-model-rename.md` (the handoff copy for the other login) to match
  this plan.

## Verification

1. `dotnet build Saga.slnx` then `dotnet test` — all 60 tests green, including the new
   `PricingTests` cached-rate cases.
2. Run the app (`.claude/skills/run-saga`, `http://localhost:5033`). With the dev defaults it must
   still auto-sign-in, upload via real Content Understanding, and generate via the fake AI.
3. In a proposal: upload a document → generate the artifact chain → open the **Usage** tab.
   Expect non-zero DKK costs (dev `fake-model` rates × 6.9) and `fake-model` in the model column;
   the extraction row should show `prebuilt-layout`.
4. `/admin` → the Service × Model breakdown shows the same totals, header reads "Est. cost (DKK)".
5. Flip `Ai:UseFakeExtractor` to `true` in `appsettings.Development.json`, restart, upload a PDF —
   it must parse offline with no Content Understanding call (no new `prebuilt-layout` usage row).
6. Sanity-check the rename with `grep -rn "gpt-5.4" --include=*.cs --include=*.json --include=*.razor
   --include=*.md --include=*.ps1 .` — expect no hits outside `bin`/`obj`.
7. Deferred to provisioning (TODO item 2), since it needs a real endpoint: one generation against
   `gpt-5.6-terra`, then check the Usage tab's per-call log shows non-zero input/output tokens and
   a non-zero cached count on the second and later calls of the run. Zero tokens on a real call
   means the streamed-usage opt-in from section 3 is still missing.

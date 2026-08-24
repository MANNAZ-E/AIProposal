# Usage tab: scrollable call modal + cached-input statistics

## Context

Two problems on the workspace **Usage** tab (`UsageSection.razor`):

1. **The call-detail modal cannot be scrolled.** `.modal-backdrop` is `position: fixed; inset: 0`
   with no `overflow-y`, and `.modal-panel` / `.modal-wide` have no `max-height` and no `overflow`.
   The usage modal is the only one that stacks up to four `.preview-text` blocks (Error,
   Instruction, Request, Response), each capped at `55vh`. When the request is long the panel grows
   far past the viewport bottom, and the Response block and the Close button become unreachable —
   there is no scrollbar anywhere to get to them.

2. **Cached input tokens are recorded but never shown.** Prompt caching is the whole reason the
   system-prompt → material → instruction order is pinned down (see CLAUDE.md), and the cached
   tokens are already captured end-to-end — `AzureOpenAiService` reads
   `InputTokenDetails.CachedTokenCount`, `UsageTrackingAiService` writes
   `AiUsageRecord.CachedInputTokens`, `PricingService` bills them at `CachedInputPer1M`, and
   `AiUsageService.BreakdownAsync` / `Sum` already carry them on `AiUsageTotals`. Nothing renders
   them, so there is no way to confirm from the UI that caching is actually being hit.

Outcome: the modal is readable however long the prompt is, and every usage surface shows how much
of the input was served from cache.

**Semantics that must not be broken:** `CachedInputTokens` is a *subset of* `InputTokens`, not
additional to it (`PricingService.EstimateLlmUsd` clamps and subtracts). The UI must never add the
two together, and the column belongs next to "Tokens in", labelled as a share of it.

Note: `FakeAiService` reports no cached tokens (all five `AiStreamEvent.Completed(...)` sites use
the 3-arg overload). Per the decision taken while planning, it stays that way — the new figures
read `—` under `Ai:UseFakeAi: true` and only show real numbers against Azure, which is what dev
calls by default anyway.

---

## Part 1 — Make the modal scrollable, shrink the preview boxes

**`src/Saga.Web/wwwroot/app.css`**

- `.modal-panel` (line 425): add `max-height: 82vh;` and `overflow-y: auto;`. This is a global
  safety net — every modal in the app (`ProposalPage`, `ChatSection`, `ContentSection`,
  `MaterialSection`, `ProseArtifactSection`, `ReviewSection`, `StructureSection`, `UsageSection`)
  gains a scrollbar instead of bleeding off the bottom, and no markup changes anywhere.
- `.modal-backdrop` (line 414): the panel starts `10vh` down, so `10vh + 82vh` leaves only `8vh`
  of slack. Replace `padding-top: 10vh;` with `padding: 8vh 1rem;` so a full-height panel is inset
  symmetrically and never touches the viewport edge.
- `.preview-text` (line 719): drop `max-height` from `55vh` to `20rem` (~320px). Four stacked
  blocks then fit sensibly inside the capped panel, and the existing per-block `overflow-y: auto`
  keeps each one independently scrollable.

Deliberately *not* doing: a flex-column panel with a pinned header/footer. That needs a
`.modal-body` wrapper element added to every modal in the app for a marginal UX gain; scrolling the
whole panel reaches the Close button perfectly well.

## Part 2 — Surface cached input tokens

### Shared formatting helper

New **`src/Saga.Web/Tokens.cs`**, alongside the existing `src/Saga.Web/Money.cs` (same pattern: a
tiny static Web-layer formatter, XML-doc'd with the *why*):

```csharp
/// <summary>
/// Renders the cached slice of a call's input tokens. Cached tokens are part of the input count,
/// never additional to it, so they are always shown as a share of it — the share is the number
/// that says whether prompt caching is working at all. "—" when no input was billed, which is
/// every Content Understanding row and any call the provider sent no usage for.
/// </summary>
public static string Cached(long cachedTokens, long inputTokens)
    => inputTokens <= 0 ? "—" : $"{cachedTokens:N0} ({(double)cachedTokens / inputTokens:P0})";
```

### Service layer

**`src/Saga.Infrastructure/Services/AiUsageService.cs`**

- `AiUsageTotals` (line 14) already has `CachedInputTokens` — no change.
- `AiUsageCall` (line 24): add `int CachedInputTokens` positionally, right after `InputTokens`.
  Update both construction sites: the LINQ projection in `GetProposalCallsAsync` (~line 87) and the
  manual construction in `GetCallDetailAsync` (~line 106).

**`src/Saga.Infrastructure/Services/AdminService.cs`**

- `ProposalSpend` (line 8): add `long CachedInputTokens` after `InputTokens`.
- `GetUsageAsync` projection (~line 60): add
  `CachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),` and pass it through in the
  `ProposalSpend` construction (~line 71).

No migration — the column already exists (`20260824084221_AiUsageRecords`, non-nullable `int`,
default 0).

### UI

**`src/Saga.Web/Components/Proposal/UsageSection.razor`**

- **Summary tiles** (lines 26–50): add a "Cached input" tile after "Tokens in / out", rendering
  `@Tokens.Cached(_usage.Totals.CachedInputTokens, _usage.Totals.InputTokens)`. This is the
  headline number the tab was missing.
- **"By service and model" table** (lines 52–93): insert a `Cached in` header after `Tokens in`,
  and a right-aligned cell in both the group row and the per-model row using the same helper
  against `groupTotal` / `row.Totals`.
- **Call log table** (lines 104–142): insert a `Cached` header after `In / out`, and a cell
  rendering `@Tokens.Cached(call.CachedInputTokens, call.InputTokens)`.
- **Call-detail modal meta line** (lines 151–162): append `· @Tokens.Cached(...) cached` after the
  cost, so a single call can be checked without hunting the log row.

**`src/Saga.Web/Components/Pages/AdminPage.razor`**

- "Usage by service" table (lines 80–119): same `Cached in` column as above — pure view change, the
  data is already on `AiUsageBreakdownRow.Totals`.
- "Usage by proposal" table (lines 128–169): `Cached in` column in header, body, and the `<tfoot>`
  totals row (`@Tokens.Cached(_usage.Sum(u => u.CachedInputTokens), _usage.Sum(u => u.InputTokens))`).
  Both tables gain one `<th>`/`<td>`, so the `colspan="2"` group cell and the empty `<th>` padding
  cells in the footer stay consistent.

Formatting conventions to match throughout: `.ToString("N0")` for counts,
`style="text-align: right;"` on numeric cells, `"—"` for absent values.

---

## Verification

1. `dotnet build Saga.slnx` — catches every miss from the two positional record changes
   (`AiUsageCall`, `ProposalSpend`).
2. `dotnet test` — no test references `AiUsageTotals`, `AiUsageCall`, `ProposalSpend` or
   `CachedInputTokens`, so nothing should break; run it to confirm the record-arity changes did not
   ripple.
3. Run the app via the **run-saga** skill (`http://localhost:5033`) against a proposal that already
   has real Azure calls logged:
   - Usage tab → the new tile and both new columns show non-zero cached figures with a percentage.
   - Open a call in the log whose request is long → the modal is capped, the panel scrolls, the
     Response block and the Close button are both reachable, and each preview box is ~20rem tall
     with its own scrollbar.
   - Check one more modal elsewhere (e.g. a document preview in **Material**) to confirm the global
     `.modal-panel` cap did not visually clip a short modal.
   - `/admin` → both tables show the `Cached in` column and the footer total lines up.
4. Sanity-check the arithmetic on one row: cached ≤ input, and the percentage matches
   `cached / input`.

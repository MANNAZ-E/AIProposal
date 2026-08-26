# Saga — CLAUDE.md

Saga is an ASP.NET Core Blazor Server app (.NET 10) that generates and reviews
proposal documents. Solution: `Saga.slnx` (`src/Saga.Core`, `src/Saga.Infrastructure`,
`src/Saga.Web`, `tests/Saga.Tests`).

## UI

- **Do not add explanatory help text to new features.** No intro paragraph under a
  heading explaining what the section does, no caption spelling out what a control
  accepts. The consultants using Saga know their job; a label and a placeholder are
  enough, and prose that only restates the visible UI is deleted as soon as it ships.
- **Windows button order: the action goes left, Cancel right.** In every dialog the
  affirmative button (Save, Delete, Generate, Restore…) comes first in the markup and
  Cancel/Close/Back last, so the group reads action-then-dismiss inside the
  right-aligned `.modal-actions` row. Saga runs on Windows desktops; matching the
  platform matters more than the web's habit of putting Cancel first.
- **The router is interactive; individual components must not declare a render mode.**
  `App.razor` sets `@rendermode="InteractiveServer"` on `<Routes />` and `<HeadOutlet />`, so the
  whole app is one interactive subtree and a `@rendermode` on a page or component inside it is a
  runtime error. This is what keeps navigation from flickering: the workspace tabs are real links,
  and a statically-rendered router would turn each click into an HTTP request that reloads the
  document and flashes the page white on its way through "Loading…". With the router in the circuit
  a tab click only changes the URL, and because every section resolves to the same `ProposalPage`
  component the instance survives — only the content pane re-renders. `HeadOutlet` needs the mode
  too, or `<PageTitle>` freezes at whatever the first render set. Links that must leave Blazor
  need `target="_blank"` (the export downloads) or `forceLoad: true` (sign-out), or the client-side
  router intercepts them and looks for a route that isn't there.

- **Dialogs answer the keyboard.** Opening one puts the caret in its first field
  (an `_focusX` flag set on open, honoured in `OnAfterRenderAsync` — the `autofocus`
  attribute does nothing for markup patched into an already-parsed page). Escape backs
  out, Ctrl+Enter commits, and Enter in a title box moves to the body. `MaterialSection`'s
  `DialogKeyAsync` on the modal panel is the pattern; a dialog with no field of its own
  needs `tabindex="-1"` on the panel and focus moved there, or the keydown never fires.

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
- Rates live in the `Pricing` config section, **in USD** (what Azure publishes); `Pricing:UsdToDkk`
  converts for display only. Cost is frozen on the row at write time. `PricingService` returns 0 for
  an unpriced model rather than throwing — metering must never break a generation, which is also why
  `PricingConfigurationTests` guards the shipped rate keys: an unpriced meter is silent by design.
- LLM rates are keyed by **deployment name**. Content Understanding rates are keyed by **meter** —
  `DocumentPagesMinimalPer1000` / `Basic` / `Standard` — never by analyzer, because the service
  charges for the work it actually performed: `prebuilt-layout` bills **Minimal** ($0.01/1000) on a
  digital Office file and **Standard** ($5.00/1000) on a PDF, an image, or a screenshot lifted out of
  a .docx. One upload routinely hits both. West Europe list prices, from the Azure retail price feed.
- **Every prompt is assembled system prompt → material → instruction**, and new call sites must keep
  that order. The system prompt holds only what is stable for the proposal (persona, voice, language,
  source rules); the client material comes next; the task, the output contract and any steering the
  consultant typed go in a trailing user message. Anything variable placed ahead of the material
  shifts every byte of the tender and re-charges it at full input price — which is why a per-unit task
  in the system prompt cost the whole working context once per slide. `PromptOrderTests` pins this
  down; the order is invisible in the output, so nothing else would catch a regression.
- Input tokens the provider served from its cache are billed at `CachedInputPer1M`, a fraction of
  the input rate, and only the uncached remainder at `InputPer1M` — it matters because the system
  prompt and working context repeat across every call of a run. Omitting the key falls back to the
  full input rate. A *succeeded* call reporting zero tokens means the provider sent no usage data,
  not that it was free, so `UsageTrackingAiService` logs a warning rather than banking the zero.
- Visible per proposal on the workspace **Usage** tab (`UsageSection.razor`) and across all
  proposals on `/admin`. Dev prices for `fake-model` are set in `appsettings.Development.json`
  so local numbers are non-zero.

## Document extraction

Uploads go through `CompositeTextExtractor`: local text files to `PlainTextExtractor` (free,
unmetered), everything else to Content Understanding behind the usage decorator.

- **Office files are not OCR'd by the analyzer.** `prebuilt-layout` reads a PDF or an image upload
  visually, but takes the native digital-extraction path for `.docx`/`.pptx`/`.xlsx` and never looks
  inside embedded bitmaps — it leaves an empty `![](figures/1.1)` where each one stood. A tender
  exported from a procurement portal is often nothing but such screenshots, so its questions used to
  vanish entirely. Switching analyzer does not help; figure handling is PDF/image-only everywhere.
- `EmbeddedImageTextExtractor` fixes that by handing each embedded image back to the *same* analyzer
  as an image, where OCR does run, and splicing the result over its placeholder. It is registered
  **outside** `UsageTrackingTextExtractor` on purpose: every per-image call is then metered as its
  own row sharing the document's `OperationId`, rather than disappearing into the document's row.
  It is skipped under `Ai:UseFakeExtractor` — the stand-in answers figures with the same placeholder
  prose as documents.
- Reading order is what makes this work: image *n* must be the *n*-th one a reader sees, or the text
  lands on the wrong figure. `OfficeImageReader` therefore walks the relationship references inside
  the content (`SldIdLst` order for slides, not `SlideParts`) instead of enumerating `ImageParts`,
  which comes back in arbitrary package order. If the placeholder and image counts still disagree,
  the recovered text is appended with a warning rather than placed — a requirement attributed to the
  wrong page is worse than one that lost its position.
- A figure whose OCR comes back with almost no text is a chart, diagram or photo rather than a
  screenshot, and gets one vision call instead (`FigurePrompts`, Light tier, `AiOperation.DescribeFigure`),
  marked `*Figure description:*` so a later model does not read it as the client's own wording. That
  is the only place `AiMessage.Images` is used; everything else is text-only.
- The splice shifts the page map with it (`FigureSplicer`), because `DocumentChunker` reads only what
  the spans cover and requirement sources are labelled from them — text outside every span would be
  dropped from the pipeline silently. Note that Content Understanding reports **no page spans at all
  for Office files** (measured: the SKA docx comes back with 0 spans), so for Word the shift is a
  no-op and requirement sources fall back to "part 1". It is PDFs that carry a real page map.
- **Page geometry is not the billing unit** — reading it as one is what metered every Office upload
  as free. `ContentUnderstandingExtractor` takes the quantities from `operation.GetUsage()`, the
  `usage` object Azure returns beside its result, and `document.Pages` is now used only to build the
  page map. Nothing on our side may derive a page count: Word bills by its own native pagination
  (PPTX per slide, XLSX per sheet including hidden ones, TXT/HTML per 3,000 characters), none of
  which is reproducible from OpenXML. A call that reports no usage records **null**, not zero, and
  logs a warning — "we were not told" is a different fact from "nothing was charged".
- The billed quantities are `documentPages{Minimal,Basic,Standard}` plus `contextualizationTokens`
  — the only fields of the SDK's `UsageDetails` a `prebuilt-layout` document call can populate
  (`audioHours`/`videoHours` and the per-model `tokens` dictionary belong to media and to
  generative analyzers, so nothing reads them). **The async path is what earns the cheap meters:**
  `AnalyzeBinaryAsync(WaitUntil.Completed, …)` bills `Doc Content Extraction *`, while the sync
  API bills separate `Doc Co Extr Sync *` meters at 1.5× ($1.50 and $7.50 per 1,000 against $1.00
  and $5.00). The `usage` object reads identically either way, so switching to the sync call would
  under-bill every row with no test failing.
- Tuning knobs live in the `Extraction` config section (`EmbeddedImageOptions`): `MinBytes` skips
  icons and logo chips, `MaxImages` caps paid calls per document, `MinTextChars` is the
  OCR-or-describe threshold. Identical images are read once however often they repeat.

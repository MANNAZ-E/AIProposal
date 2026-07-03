# Saga — remaining work

Status 2026-07-03: all 10 plan milestones are built and committed (M1–M10).
All 60 unit tests pass; every milestone was verified end-to-end in the running app
against LocalDB and the offline stand-in AI (no Azure endpoints configured locally).

## Needs Emil

1. **Sample PPTX/DOCX** — provide a representative Mannaz deck (and Word doc) so the
   export styling can be changed from the clean #5A616D fallback to mimic the real
   Mannaz look (colors, fonts, layout approximation).
2. **Azure provisioning** — *planned pickup: after summer 2026.*
   Run `scripts/provision-azure.ps1` (scripted version of `docs/azure-provisioning.md`,
   written 2026-07-03, reviewed but NOT yet executed). Before running, edit the
   variables at the top:
   - `$AppServicePlan` (+ `$PlanResourceGroup`) — the existing West Europe plan's name.
   - `$StrongModelVersion` / `$LightModelVersion` — look up current gpt-5.4 /
     gpt-5.4-mini versions (`az cognitiveservices model list`); if unavailable in
     West Europe, change `$AiLocation` (e.g. `swedencentral`).
   - Resource names (`$WebAppName`, `$SqlServerName`, `$StorageAccount`) — globally unique.
   The Entra app registration IDs are already filled in (client
   `eca5258b-7242-41cc-8416-ef5d8d8d9696`, tenant `6443a88d-b20d-4c72-8654-f76c5e407909`).
   Step 4c (SQL user for the managed identity) needs sqlcmd with token auth, or paste
   the T-SQL into the portal Query Editor. Step 10 (first migration + code deploy) is
   commented out in the script — run it manually per the comments.
3. **Real Entra sign-in test** — set `Auth:DevAutoSignIn=false` with the `AzureAd`
   section filled in and verify sign-in with a Mannaz account (dev keeps auto-sign-in).
4. **Open exports in Office** — the PPTX/DOCX pass the OpenXML validator and carry the
   right content, but nobody has visually opened them in PowerPoint/Word yet.

## Follow-up development

5. **Bing grounding for the client profile** — wire the Foundry "Grounding with Bing
   Search" connection into `IWebResearchService` (currently `NullWebResearchService`;
   the client profile generates from uploaded material only, with a caveat line).
6. **Real-model quality pass** — once the Foundry endpoint is configured, tune prompts
   and requirements-extraction chunking against a golden (anonymized) tender; keep it
   as the standing quality benchmark.
7. **Token prices** — enter current €/1M token prices in configuration
   (`AzureOpenAI:StrongPrice` / `LightPrice`) so the Admin usage page shows real costs
   instead of zeros.
8. **Production EF migrations** — migrations auto-apply only in Development; decide the
   prod approach for first deploy (run `dotnet ef database update` as the Entra admin,
   or temporarily set the environment to Development — see checklist step 8).

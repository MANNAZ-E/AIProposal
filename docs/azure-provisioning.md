# Saga — Azure provisioning checklist

Manual portal setup, step by step. All resources in **West Europe** (the existing App Service
Plan's region). Names are suggestions — adjust to Mannaz conventions.

## 1. App Service

1. Portal → **App Services** → **Create** → Web App.
   - Name: `saga-mannaz` (URL becomes `https://saga-mannaz.azurewebsites.net`).
   - Publish: Code · Runtime: **.NET 10** · OS: per existing plan · Region: **West Europe**.
   - App Service Plan: pick the **existing West Europe plan**.
2. After creation: **Settings → Identity → System assigned → On** → Save.
   Note the **Object (principal) ID** — the role assignments below need it.
3. **Configuration → General settings**: HTTPS Only = On.

## 2. Azure SQL (Managed Identity auth)

1. **SQL databases** → **Create**.
   - Server: create `saga-sql-mannaz` (West Europe). Authentication: **Microsoft Entra-only**
     is fine (no SQL logins needed). Set yourself as Entra admin.
   - Database: `Saga`. Start small (e.g. Basic/S0 or serverless) — the data is tiny.
2. **Networking**: allow Azure services, or better: add the App Service's outbound IPs.
3. Create the app's database user. In the portal **Query editor** (signed in as the Entra
   admin) against the `Saga` database, run:
   ```sql
   CREATE USER [saga-mannaz] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_owner ADD MEMBER [saga-mannaz];
   ```
   (`saga-mannaz` = the App Service name; `db_owner` is pragmatic for v1 since EF migrations
   run from the app at startup — tighten later to `db_datareader/db_datawriter/db_ddladmin`.)
4. Connection string for the app (no secret):
   ```
   Server=tcp:saga-sql-mannaz.database.windows.net,1433;Database=Saga;Authentication=Active Directory Managed Identity;Encrypt=True;
   ```

## 3. Storage account (uploads)

1. **Storage accounts** → **Create**: `sagamannazstorage`, West Europe, Standard LRS.
2. No public blob access. The app creates the `uploads` container itself.
3. **Access control (IAM)** → **Add role assignment** →
   Role: **Storage Blob Data Contributor** → Assign to the **saga-mannaz** managed identity.

## 4. Azure AI Foundry (models + document parsing)

1. **Azure AI Foundry** → create a resource/project `saga-ai-mannaz` in **West Europe**.
   *Check GPT 5.4 availability in West Europe first — if unavailable, use the nearest EU
   region (e.g. Sweden Central); the resource region may differ from the app's.*
2. Deploy two models (Deployments → Deploy model):
   - **gpt-5.4** — deployment name `gpt-5.4` (analysis, generation, chat, review).
   - **gpt-5.4-mini** — deployment name `gpt-5.4-mini` (requirements extraction, condensation).
   Deployment names must match `AzureOpenAI:StrongDeployment` / `LightDeployment` app settings.
3. **Access control (IAM)** → Role: **Cognitive Services OpenAI User** → assign to the
   **saga-mannaz** managed identity.
4. Note the endpoint (e.g. `https://saga-ai-mannaz.openai.azure.com/`). Leave `AzureOpenAI:Key`
   empty — the app then authenticates with the managed identity.
5. *(Optional, client-profile web research)*: set up **Grounding with Bing Search** in the
   Foundry project and note its connection — wiring the app to it is a follow-up task; without
   it the client profile generates from the uploaded material only.

## 5. Content Understanding (document parsing)

Runs on the **same Foundry resource** as step 4 — there is no separate resource to create, and
the `prebuilt-layout` analyzer the app uses needs **no model deployment** (it is content
extraction only; the gpt-4.1/embedding deployments in Microsoft's docs are for the RAG analyzers).

1. **Access control (IAM)** on `saga-ai-mannaz` → Role: **Cognitive Services User** → assign to
   the **saga-mannaz** managed identity. The *OpenAI User* role from step 4 does not cover
   Content Understanding; this second assignment is required.
2. Check that West Europe is still on the
   [supported region list](https://learn.microsoft.com/azure/ai-services/content-understanding/language-region-support).
3. `ContentUnderstanding:Endpoint` is the resource endpoint from step 4 — the account-level
   `https://saga-ai-mannaz.cognitiveservices.azure.com/` (or its `services.ai.azure.com` alias),
   **not** a `/api/projects/<project>` URL. There is no key setting: the app always uses Entra ID.
4. Uploaded material is sent with `processingLocation=geography`, so it is processed inside the
   resource's geography rather than the service's "global" default.

## 6. Entra ID app registration (sign-in)

Using the **existing app registration**:

1. **Authentication** → Add platform → **Web**:
   - Redirect URI: `https://saga-mannaz.azurewebsites.net/signin-oidc`
   - Front-channel logout URL: `https://saga-mannaz.azurewebsites.net/signout-oidc`
   - Enable **ID tokens**.
2. **Certificates & secrets** → New client secret. Copy the value immediately.
3. Note the **Application (client) ID** and **Directory (tenant) ID**.

## 7. App Service configuration

App Service → **Environment variables / App settings** (colon `:` becomes double underscore `__`):

| Setting | Value |
|---|---|
| `ConnectionStrings__Saga` | the SQL connection string from step 2.4 |
| `Auth__DevAutoSignIn` | `false` |
| `AzureAd__TenantId` | tenant id from step 6 |
| `AzureAd__ClientId` | client id from step 6 |
| `AzureAd__ClientSecret` | secret from step 6 (or reference a Key Vault secret) |
| `Storage__BlobServiceUri` | `https://sagamannazstorage.blob.core.windows.net` |
| `AzureOpenAI__Endpoint` | Foundry endpoint from step 4 |
| `AzureOpenAI__StrongDeployment` | `gpt-5.4` |
| `AzureOpenAI__LightDeployment` | `gpt-5.4-mini` |
| `AzureOpenAI__StrongPrice__InputPer1M` | current €/1M input tokens (usage page estimates) |
| `AzureOpenAI__StrongPrice__OutputPer1M` | current €/1M output tokens |
| `AzureOpenAI__LightPrice__InputPer1M` | current €/1M input tokens (mini) |
| `AzureOpenAI__LightPrice__OutputPer1M` | current €/1M output tokens (mini) |
| `ContentUnderstanding__Endpoint` | Foundry endpoint from step 4 (same value as `AzureOpenAI__Endpoint`) |

Leave `AzureOpenAI__Key` **unset** — empty key = managed identity. Content Understanding has no
key setting at all.

## 8. Deploy and smoke test

1. Publish: `dotnet publish src/Saga.Web -c Release` and deploy (VS publish, `az webapp deploy`,
   or GitHub Actions later). EF migrations currently auto-run only in Development — for the
   first deployment either run `dotnet ef database update` against Azure SQL from a dev
   machine signed in as the Entra admin, or temporarily set `ASPNETCORE_ENVIRONMENT=Development`.
2. Browse the site → Entra sign-in should challenge → sign in with a Mannaz account.
3. Create a test proposal → upload a PDF (Content Understanding path) → generate the chain
   (real GPT 5.4) → chat → review → export both formats and open them in Office.
4. Share the proposal with sda@mannaz.com and verify role behavior.
5. Check the Admin page: usage rows with non-zero tokens, and set the Mannaz voice.

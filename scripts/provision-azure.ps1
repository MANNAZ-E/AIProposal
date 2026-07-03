# =============================================================================
# Saga — Azure provisioning script (Azure CLI, PowerShell)
# =============================================================================
# Scripted version of docs/azure-provisioning.md. Read top to bottom before
# running — every step says what it creates and why.
#
# PREREQUISITES
#   - Azure CLI installed (az --version) and signed in:  az login
#   - You have Owner (or Contributor + User Access Administrator) on the
#     subscription: the script creates resources AND role assignments.
#   - The correct subscription is selected:
#       az account show --query name
#       az account set --subscription "<name-or-id>"
#   - sqlcmd available (ships with SQL Server tools / comes with LocalDB setup)
#     for the one step az cannot do: creating the database user (step 4c).
#
# WHAT THIS SCRIPT DOES (in order)
#   1. Variables — all names in one place; edit before running.
#   2. Resource group (skipped if you want everything in an existing one).
#   3. App Service on the EXISTING West Europe plan + system-assigned identity.
#   4. Azure SQL: server (Entra-only auth), database, firewall, DB user for the
#      app's managed identity (T-SQL via sqlcmd + your Entra token).
#   5. Storage account + "Storage Blob Data Contributor" for the app identity.
#   6. Azure AI Foundry (Azure OpenAI) + the two model deployments + role.
#   7. Document Intelligence + role.
#   8. Entra app registration: redirect URIs, ID tokens, client secret.
#   9. App Service configuration (connection string + app settings).
#  10. (Commented out) code deployment.
#
# NOTHING here deletes anything. Creates are idempotent-ish: re-running mostly
# updates in place, but 'az ad app credential reset' makes a NEW secret each
# time, and role assignments print a harmless "already exists" error.
# =============================================================================

$ErrorActionPreference = "Stop"

# =============================================================================
# 1. VARIABLES — EDIT THESE FIRST
# =============================================================================

# --- Entra app registration (already created by Emil) ------------------------
$TenantId        = "6443a88d-b20d-4c72-8654-f76c5e407909"   # Directory (tenant) ID
$AppClientId     = "eca5258b-7242-41cc-8416-ef5d8d8d9696"   # Application (client) ID
$AppObjectId     = "6abd8de8-3e74-42eb-aebe-9c6d43c355fa"   # Object ID of the app registration
                                                            # (az ad app update accepts either id;
                                                            # we use the client id below)

# --- Locations ----------------------------------------------------------------
$Location        = "westeurope"      # everything except (possibly) the AI models
$AiLocation      = "westeurope"      # CHECK FIRST: if GPT 5.4 / 5.4-mini are not
                                     # available in West Europe, set e.g.
                                     # "swedencentral" — only the AI resource moves,
                                     # the app stays in West Europe.
# To check model availability before running:
#   az cognitiveservices model list -l westeurope --query "[?model.name=='gpt-5.4'].model.version" -o tsv

# --- Resource names (adjust to Mannaz conventions) ----------------------------
$ResourceGroup   = "rg-saga"                 # set to your EXISTING group to reuse one
$CreateResourceGroup = $true                 # $false if $ResourceGroup already exists

$AppServicePlan  = "<EXISTING-PLAN-NAME>"    # REQUIRED: the existing West Europe plan's name
$PlanResourceGroup = $ResourceGroup          # ...and the resource group that plan lives in,
                                             # if different from $ResourceGroup

$WebAppName      = "saga-mannaz"             # becomes https://saga-mannaz.azurewebsites.net
                                             # (must be globally unique on azurewebsites.net)
$SqlServerName   = "saga-sql-mannaz"         # must be globally unique on database.windows.net
$SqlDbName       = "Saga"
$StorageAccount  = "sagamannazstorage"       # 3-24 chars, lowercase+digits, globally unique
$AiAccountName   = "saga-ai-mannaz"          # Azure OpenAI / Foundry resource
$DocIntelName    = "saga-docintel-mannaz"    # Document Intelligence resource

# --- SQL Entra admin: who can administer the SQL server -----------------------
# Defaults to YOU (the signed-in az user). The server is created Entra-only —
# no SQL logins/passwords exist at all.
$SqlAdminUpn     = az ad signed-in-user show --query userPrincipalName -o tsv
$SqlAdminObjectId = az ad signed-in-user show --query id -o tsv

# --- Model deployments ---------------------------------------------------------
# Deployment NAMES must match the app settings AzureOpenAI__StrongDeployment /
# __LightDeployment (the app defaults to exactly these names).
$StrongModelName    = "gpt-5.4";      $StrongDeployment = "gpt-5.4"
$LightModelName     = "gpt-5.4-mini"; $LightDeployment  = "gpt-5.4-mini"
# Model VERSION differs per region/date — look it up right before running:
#   az cognitiveservices model list -l $AiLocation -o table | Select-String "gpt-5.4"
$StrongModelVersion = "<CHECK-AND-SET>"      # e.g. "2026-05-01"
$LightModelVersion  = "<CHECK-AND-SET>"
# Capacity = throughput units (TPM in thousands for Standard). 50 is a sane start.
$ModelCapacity      = 50

# --- Token prices for the usage page (optional, can set later in the portal) --
# Whatever currency you enter here is the currency the Admin usage page shows.
$StrongPriceInPer1M  = "0"   # e.g. "2.50"
$StrongPriceOutPer1M = "0"   # e.g. "10.00"
$LightPriceInPer1M   = "0"
$LightPriceOutPer1M  = "0"

Write-Host "Subscription: $(az account show --query name -o tsv)"
Write-Host "About to provision into resource group '$ResourceGroup' ($Location)."
Read-Host  "Press Enter to continue, Ctrl+C to abort"

# =============================================================================
# 2. RESOURCE GROUP
# =============================================================================
if ($CreateResourceGroup) {
    az group create --name $ResourceGroup --location $Location | Out-Null
    Write-Host "✓ Resource group $ResourceGroup"
}

# =============================================================================
# 3. APP SERVICE (on the existing plan) + MANAGED IDENTITY
# =============================================================================
# Creates the web app on the EXISTING plan — nothing new is billed here beyond
# what the plan already costs. Runtime .NET 10, HTTPS only.
az webapp create `
    --name $WebAppName `
    --resource-group $ResourceGroup `
    --plan $(az appservice plan show -n $AppServicePlan -g $PlanResourceGroup --query id -o tsv) `
    --runtime "DOTNETCORE:10.0" | Out-Null

az webapp update --name $WebAppName --resource-group $ResourceGroup --https-only true | Out-Null

# System-assigned managed identity: this is the identity that gets database
# access, blob access, and AI access — no secrets/connection-string passwords.
$PrincipalId = az webapp identity assign `
    --name $WebAppName --resource-group $ResourceGroup `
    --query principalId -o tsv
Write-Host "✓ Web app $WebAppName (managed identity: $PrincipalId)"

# =============================================================================
# 4. AZURE SQL — Entra-only auth, managed-identity database user
# =============================================================================

# 4a. Server with Entra-ONLY authentication (no SQL passwords exist), you as admin.
az sql server create `
    --name $SqlServerName `
    --resource-group $ResourceGroup `
    --location $Location `
    --enable-ad-only-auth `
    --external-admin-principal-type User `
    --external-admin-name $SqlAdminUpn `
    --external-admin-sid $SqlAdminObjectId | Out-Null

# Database: serverless with auto-pause — the data is tiny, this is the cheapest
# sensible tier. Adjust --capacity / tier if you prefer a fixed S0.
az sql db create `
    --name $SqlDbName `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --edition GeneralPurpose `
    --family Gen5 `
    --compute-model Serverless `
    --capacity 1 `
    --auto-pause-delay 60 `
    --zone-redundant false | Out-Null
Write-Host "✓ SQL server $SqlServerName / database $SqlDbName"

# 4b. Firewall: allow Azure services (the App Service's outbound traffic).
# 0.0.0.0 is the special "Azure services" rule, not the public internet.
az sql server firewall-rule create `
    --resource-group $ResourceGroup --server $SqlServerName `
    --name AllowAzureServices `
    --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 | Out-Null

# ...and your current IP so this script (and later dev tools) can reach it:
$MyIp = (Invoke-RestMethod "https://api.ipify.org")
az sql server firewall-rule create `
    --resource-group $ResourceGroup --server $SqlServerName `
    --name "provisioning-$env:COMPUTERNAME" `
    --start-ip-address $MyIp --end-ip-address $MyIp | Out-Null

# 4c. THE ONE STEP az CANNOT DO: create the database user for the app's
# managed identity. This runs T-SQL against the database, authenticated with
# YOUR Entra token (you are the server admin from 4a).
#   CREATE USER [saga-mannaz] FROM EXTERNAL PROVIDER  <- name = web app name
#   db_owner is pragmatic for v1 because EF migrations run from the app;
#   tighten to datareader/datawriter/ddladmin once the schema is stable.
$Token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
$CreateUserSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$WebAppName')
    CREATE USER [$WebAppName] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner ADD MEMBER [$WebAppName];
"@
$CreateUserSql | sqlcmd -S "$SqlServerName.database.windows.net" -d $SqlDbName -G -P $Token --authentication-method ActiveDirectoryAccessToken
# If your sqlcmd version does not support access-token auth, run the T-SQL above
# manually in the portal Query Editor (signed in as yourself) instead.
Write-Host "✓ Managed identity '$WebAppName' is db_owner on $SqlDbName"

# =============================================================================
# 5. STORAGE ACCOUNT (uploads) + BLOB ROLE
# =============================================================================
# No public blob access; the app creates its 'uploads' container on first use.
az storage account create `
    --name $StorageAccount `
    --resource-group $ResourceGroup `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --allow-blob-public-access false | Out-Null

# The app writes/reads/deletes blobs as its managed identity:
az role assignment create `
    --assignee-object-id $PrincipalId `
    --assignee-principal-type ServicePrincipal `
    --role "Storage Blob Data Contributor" `
    --scope $(az storage account show -n $StorageAccount -g $ResourceGroup --query id -o tsv) | Out-Null
Write-Host "✓ Storage $StorageAccount + blob role for the app"

# =============================================================================
# 6. AZURE AI FOUNDRY (Azure OpenAI) + MODEL DEPLOYMENTS + ROLE
# =============================================================================
# kind AIServices = the Foundry multi-service resource. --custom-domain is
# required for Entra-token (managed identity) auth to work against it.
az cognitiveservices account create `
    --name $AiAccountName `
    --resource-group $ResourceGroup `
    --location $AiLocation `
    --kind AIServices `
    --sku S0 `
    --custom-domain $AiAccountName | Out-Null

# The two deployments the app expects (names must match app settings):
#   gpt-5.4      -> analysis, generation, chat, review   (Strong)
#   gpt-5.4-mini -> requirements extraction, condensation (Light)
az cognitiveservices account deployment create `
    --name $AiAccountName --resource-group $ResourceGroup `
    --deployment-name $StrongDeployment `
    --model-name $StrongModelName --model-version $StrongModelVersion --model-format OpenAI `
    --sku-name Standard --sku-capacity $ModelCapacity | Out-Null

az cognitiveservices account deployment create `
    --name $AiAccountName --resource-group $ResourceGroup `
    --deployment-name $LightDeployment `
    --model-name $LightModelName --model-version $LightModelVersion --model-format OpenAI `
    --sku-name Standard --sku-capacity $ModelCapacity | Out-Null

# The app calls the models as its managed identity (we leave AzureOpenAI__Key
# empty later, which switches the app to DefaultAzureCredential):
az role assignment create `
    --assignee-object-id $PrincipalId `
    --assignee-principal-type ServicePrincipal `
    --role "Cognitive Services OpenAI User" `
    --scope $(az cognitiveservices account show -n $AiAccountName -g $ResourceGroup --query id -o tsv) | Out-Null

$AiEndpoint = az cognitiveservices account show -n $AiAccountName -g $ResourceGroup --query properties.endpoint -o tsv
Write-Host "✓ AI Foundry $AiAccountName ($AiEndpoint) with $StrongDeployment + $LightDeployment"

# NOTE: "Grounding with Bing Search" (client-profile web research) is NOT set up
# here — it needs a Foundry project + Bing connection and app-side wiring that
# is a tracked follow-up (TODO.md item 5). Saga works without it; the client
# profile then generates from uploaded material only.

# =============================================================================
# 7. DOCUMENT INTELLIGENCE + ROLE
# =============================================================================
# Extracts text/tables/page numbers from uploaded PDFs and scans.
az cognitiveservices account create `
    --name $DocIntelName `
    --resource-group $ResourceGroup `
    --location $Location `
    --kind FormRecognizer `
    --sku S0 `
    --custom-domain $DocIntelName | Out-Null

az role assignment create `
    --assignee-object-id $PrincipalId `
    --assignee-principal-type ServicePrincipal `
    --role "Cognitive Services User" `
    --scope $(az cognitiveservices account show -n $DocIntelName -g $ResourceGroup --query id -o tsv) | Out-Null

$DocIntelEndpoint = az cognitiveservices account show -n $DocIntelName -g $ResourceGroup --query properties.endpoint -o tsv
Write-Host "✓ Document Intelligence $DocIntelName ($DocIntelEndpoint)"

# =============================================================================
# 8. ENTRA APP REGISTRATION — redirect URIs, ID tokens, client secret
# =============================================================================
# Uses the EXISTING registration (client id $AppClientId). This adds the web
# redirect URI for the deployed site and enables ID token issuance, which the
# OpenID Connect sign-in (Microsoft.Identity.Web) requires.
az ad app update --id $AppClientId `
    --web-redirect-uris "https://$WebAppName.azurewebsites.net/signin-oidc" `
    --enable-id-token-issuance true

# Front-channel logout URL (az has no dedicated flag; set via Graph PATCH):
az rest --method PATCH `
    --uri "https://graph.microsoft.com/v1.0/applications/$AppObjectId" `
    --headers "Content-Type=application/json" `
    --body "{`"web`":{`"logoutUrl`":`"https://$WebAppName.azurewebsites.net/signout-oidc`"}}"

# Client secret for the OIDC code flow. SECURITY NOTES:
#  - The secret is printed ONCE into $ClientSecret and pushed straight into the
#    App Service settings below. It is NOT written to disk by this script.
#  - Each re-run creates an ADDITIONAL secret (2-year lifetime); prune old ones
#    in the portal under Certificates & secrets.
#  - Nicer long-term: put it in Key Vault and use an app-setting reference.
$ClientSecret = az ad app credential reset `
    --id $AppClientId `
    --display-name "saga-appservice" `
    --years 2 `
    --query password -o tsv
Write-Host "✓ App registration updated (new client secret created)"

# =============================================================================
# 9. APP SERVICE CONFIGURATION
# =============================================================================
# The connection string uses the managed identity — no password anywhere.
az webapp config connection-string set `
    --name $WebAppName --resource-group $ResourceGroup `
    --connection-string-type SQLAzure `
    --settings Saga="Server=tcp:$SqlServerName.database.windows.net,1433;Database=$SqlDbName;Authentication=Active Directory Managed Identity;Encrypt=True;" | Out-Null

# App settings ("__" is the ':' hierarchy separator). AzureOpenAI__Key and
# DocumentIntelligence__Key are intentionally NOT set: an empty key makes the
# app authenticate with its managed identity instead.
az webapp config appsettings set `
    --name $WebAppName --resource-group $ResourceGroup `
    --settings `
        "Auth__DevAutoSignIn=false" `
        "AzureAd__Instance=https://login.microsoftonline.com/" `
        "AzureAd__TenantId=$TenantId" `
        "AzureAd__ClientId=$AppClientId" `
        "AzureAd__ClientSecret=$ClientSecret" `
        "AzureAd__CallbackPath=/signin-oidc" `
        "Storage__BlobServiceUri=https://$StorageAccount.blob.core.windows.net" `
        "AzureOpenAI__Endpoint=$AiEndpoint" `
        "AzureOpenAI__StrongDeployment=$StrongDeployment" `
        "AzureOpenAI__LightDeployment=$LightDeployment" `
        "AzureOpenAI__ContextTokenBudget=100000" `
        "AzureOpenAI__StrongPrice__InputPer1M=$StrongPriceInPer1M" `
        "AzureOpenAI__StrongPrice__OutputPer1M=$StrongPriceOutPer1M" `
        "AzureOpenAI__LightPrice__InputPer1M=$LightPriceInPer1M" `
        "AzureOpenAI__LightPrice__OutputPer1M=$LightPriceOutPer1M" `
        "DocumentIntelligence__Endpoint=$DocIntelEndpoint" | Out-Null
Write-Host "✓ App Service configured"

# =============================================================================
# 10. CODE DEPLOYMENT + FIRST MIGRATION (left commented on purpose)
# =============================================================================
# Migrations auto-apply only in Development. For the FIRST deploy, easiest is to
# run them from this machine as yourself (you are the SQL admin):
#
#   $env:ConnectionStrings__Saga = "Server=tcp:$SqlServerName.database.windows.net,1433;Database=$SqlDbName;Authentication=Active Directory Default;Encrypt=True;"
#   dotnet ef database update -p src/Saga.Infrastructure -s src/Saga.Web
#   Remove-Item env:ConnectionStrings__Saga
#
# Then publish and zip-deploy the app:
#
#   dotnet publish src/Saga.Web -c Release -o publish
#   Compress-Archive publish/* saga.zip -Force
#   az webapp deploy --name $WebAppName --resource-group $ResourceGroup --src-path saga.zip --type zip
#
# Smoke test (docs/azure-provisioning.md step 8): browse the site, Entra
# sign-in, create a proposal, upload a PDF, generate, chat, review, export.

Write-Host ""
Write-Host "DONE. Summary:"
Write-Host "  Site:        https://$WebAppName.azurewebsites.net"
Write-Host "  SQL:         $SqlServerName.database.windows.net / $SqlDbName (Entra-only)"
Write-Host "  Storage:     https://$StorageAccount.blob.core.windows.net (container 'uploads')"
Write-Host "  AI:          $AiEndpoint ($StrongDeployment, $LightDeployment)"
Write-Host "  Doc Intel:   $DocIntelEndpoint"
Write-Host "  Sign-in:     app $AppClientId in tenant $TenantId (new secret set on the web app)"

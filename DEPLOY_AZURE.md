# DirectShop Azure Deployment (Docker + Optional Slots + CI/CD)

This repo now supports both:
- GitHub Actions workflows with Azure App Service deployment slots
- Azure DevOps `azure-pipelines.yml` with staging deployment and production swap
- GitHub Actions publish-profile deployment route (production-first, no `AZURE_CREDENTIALS` service principal required)

Yes, deployment slots are supported for Docker containers on Azure App Service.

## What was added
- `Dockerfile`
- `.dockerignore`
- `.github/workflows/deploy-azure-webapp-container.yml` (build + deploy to staging slot)
- `.github/workflows/promote-staging-to-production.yml` (manual swap)
- `.github/workflows/rollback-production-slot.yml` (manual rollback by tag)
- `.github/workflows/deploy-staging-publish-profile.yml` (build + deploy with publish profile, production by default and staging optional)
- `.github/workflows/release-production-publish-profile.yml` (manual production release/rollback by tag using publish profile)
- `azure-pipelines.yml` (Azure DevOps equivalent)
- `Server/Program.cs` uses `ConnectionStrings:DefaultConnection`
- `Server/appsettings.json` has default connection string

## 1) Prerequisites
- Azure subscription with credits
- Azure CLI installed and logged in
- GitHub repository (for GitHub Actions path)

## 2) Create Azure resources (PowerShell)
Production-only is enough for the default publish-profile route.
Use Standard tier or above only if you want optional staging slots (`S1` recommended).

```powershell
$rg = "rg-direct-prod"
$location = "CanadaCentral"
$acr = "directacrregistry"          # unique, lowercase
$plan = "asp-direct-linux"
$app = "direct-prod-app"       # unique
$slot = "staging"                 # optional

az group create --name $rg --location $location
az acr create --resource-group $rg --name $acr --sku Basic --admin-enabled true
az appservice plan create --resource-group $rg --name $plan --is-linux --sku B1

# Create web app with a bootstrap image (pipeline will overwrite)
# --https-only true avoids policy conflicts in subscriptions enforcing HTTPS-only.
az webapp create --resource-group $rg --plan $plan --name $app --container-image-name mcr.microsoft.com/dotnet/samples:aspnetapp --https-only true

# Optional: create staging slot only when you need it (requires Standard tier or above)
# az appservice plan update --resource-group $rg --name $plan --sku S1
# az webapp deployment slot create --resource-group $rg --name $app --slot $slot
```

## 3) Configure app settings (production; staging optional)

```powershell
az webapp config appsettings set --resource-group $rg --name $app --settings WEBSITES_PORT=8080 ASPNETCORE_FORWARDEDHEADERS_ENABLED=true ConnectionStrings__DefaultConnection="Data Source=/home/data/app.db"

# Optional: apply same settings to staging slot
# az webapp config appsettings set --resource-group $rg --name $app --slot $slot --settings WEBSITES_PORT=8080 ASPNETCORE_FORWARDEDHEADERS_ENABLED=true ConnectionStrings__DefaultConnection="Data Source=/home/data/app.db"
```

Notes:
- `/home` is persistent on Linux App Service.
- SQLite is okay for low traffic/single-instance usage.
- For scale-out or heavy concurrency, move to Azure SQL.

## 4) Configure container settings once (production; staging optional)

```powershell
$acrLoginServer = "${acr}.azurecr.io"
$acrUser = (az acr credential show --name $acr --query "username" -o tsv).Trim()
$acrPass = (az acr credential show --name $acr --query "passwords[0].value" -o tsv).Trim()

if ([string]::IsNullOrWhiteSpace($acrUser) -or [string]::IsNullOrWhiteSpace($acrPass)) {
  throw "ACR admin credentials could not be read. Ensure the registry exists and admin user is enabled: az acr update --name $acr --admin-enabled true"
}

az webapp config container set --resource-group $rg --name $app --docker-custom-image-name "$acrLoginServer/direct:latest" --docker-registry-server-url "https://$acrLoginServer" --docker-registry-server-user $acrUser --docker-registry-server-password $acrPass

# Optional: configure container settings for staging slot
# az webapp config container set --resource-group $rg --name $app --slot $slot --docker-custom-image-name "$acrLoginServer/direct:latest" --docker-registry-server-url "https://$acrLoginServer" --docker-registry-server-user $acrUser --docker-registry-server-password $acrPass
```

## 5) GitHub Actions setup

### Required GitHub secrets
Add in repo settings -> Secrets and variables -> Actions:

- `ACR_LOGIN_SERVER` = `<acr-name>.azurecr.io`
- `ACR_USERNAME` = ACR username
- `ACR_PASSWORD` = ACR password
- `AZURE_RESOURCE_GROUP` = resource group name
- `AZURE_WEBAPP_NAME` = web app name
- `AZURE_WEBAPP_STAGING_SLOT` = `staging`
- `AZURE_CREDENTIALS` = service principal JSON

Create `AZURE_CREDENTIALS` JSON:

```powershell
$subId = az account show --query id -o tsv
az ad sp create-for-rbac --name "sp-direct-gh" --role Contributor --scopes "/subscriptions/$subId/resourceGroups/$rg" --json-auth
```

Copy the JSON output into `AZURE_CREDENTIALS`.

If you get `Insufficient privileges to complete the operation`, your Azure user can create resources in the subscription but cannot create App Registrations / Service Principals in Microsoft Entra ID. In that case, ask a tenant admin to do one of the following:

- run the command above for you and give you the JSON output
- grant you `Application Administrator`, `Cloud Application Administrator`, or `Global Administrator`
- enable `Users can register applications` in Microsoft Entra ID user settings

Without that Entra permission, the GitHub workflows that use `azure/login` with `AZURE_CREDENTIALS` cannot authenticate. If admin help is not available, use the publish-profile route below (production-first, staging optional).

No Visual Studio Professional is required for either route. You can complete everything from VS Code, GitHub, Azure Portal, and Azure CLI.

### Publish-profile route (no Entra admin required)

If you cannot create a service principal, use publish profiles directly with these workflows:

- `.github/workflows/deploy-staging-publish-profile.yml`
- `.github/workflows/release-production-publish-profile.yml`

Required GitHub secrets for publish-profile route:

- `ACR_LOGIN_SERVER` = `<acr-name>.azurecr.io`
- `ACR_USERNAME` = ACR username
- `ACR_PASSWORD` = ACR password
- `AZURE_WEBAPP_NAME` = web app name
- `AZURE_WEBAPP_PROD_PUBLISH_PROFILE` = full XML publish profile for production

Optional staging secrets (only if you want staging deployments):

- `AZURE_WEBAPP_STAGING_SLOT` = `staging`
- `AZURE_WEBAPP_STAGING_PUBLISH_PROFILE` = full XML publish profile for staging slot

Get publish profiles using Azure CLI (no Visual Studio needed):

```powershell
$prodProfile = az webapp deployment list-publishing-profiles --resource-group $rg --name $app --xml

# Optional: save locally so you can paste XML into GitHub secrets.
$prodProfile | Set-Content -Path .\prod.publishsettings

# Optional: only when staging slot exists.
# $stagingProfile = az webapp deployment list-publishing-profiles --resource-group $rg --name $app --slot $slot --xml
# $stagingProfile | Set-Content -Path .\staging.publishsettings
```

Or download them in Azure Portal:

- App Service -> Overview -> Get publish profile (production)
- App Service -> Deployment slots -> staging -> Get publish profile (optional)

How to deploy/promote/rollback without service principal:

- Default path: push to `main` deploys to production via `deploy-staging-publish-profile.yml`
- Optional path: run `deploy-staging-publish-profile.yml` manually and choose `target_environment = staging`
- Controlled releases/rollback: run `release-production-publish-profile.yml` with `image_tag` (`latest` or older tag)

This route avoids `azure/login` entirely and does not require staging. If you use staging, slot swaps can still be done manually in Azure Portal.

### Workflows
1. `deploy-azure-webapp-container.yml`
  - Trigger: push to `main`
  - Builds/pushes image to ACR
  - Deploys image to staging slot

2. `promote-staging-to-production.yml`
  - Trigger: manual (`workflow_dispatch`)
  - Swaps staging slot to production
  - Best used with GitHub Environment approval on `production`

3. `rollback-production-slot.yml`
  - Trigger: manual (`workflow_dispatch`)
  - Input: `image_tag` (previous commit SHA or tag)
  - Deploys that image to staging slot and swaps to production

4. `deploy-staging-publish-profile.yml`
  - Trigger: push to `main` or manual (`workflow_dispatch`)
  - On push: builds/pushes image and deploys to production
  - On manual run: choose production or optional staging target
  - If `image_tag` is provided manually, deploys that existing tag without rebuilding

5. `release-production-publish-profile.yml`
  - Trigger: manual (`workflow_dispatch`)
  - Input: `image_tag` (`latest` or previous commit SHA)
  - Deploys selected image directly to production via publish profile secret

### Deploy URL
- Production: `https://<app-name>.azurewebsites.net`
- Staging slot: `https://<app-name>-staging.azurewebsites.net`

## 6) Azure DevOps setup

`azure-pipelines.yml` is included and provides:
- Build and push to ACR
- Deploy to staging slot
- Swap staging to production

Set these pipeline variables or variable-group values:
- `azureServiceConnection`
- `azureResourceGroup`
- `webAppName`
- `acrName`
- `acrLoginServer`

Recommended:
- Configure Azure DevOps `production` environment approval for the swap stage.

## 7) Useful commands

```powershell
# List available image tags in ACR (use for rollback input)
az acr repository show-tags --name $acr --repository directshop --orderby time_desc --top 20

# Tail logs
az webapp log tail --resource-group $rg --name $app

# Restart production
az webapp restart --resource-group $rg --name $app

# Restart staging slot
# az webapp restart --resource-group $rg --name $app --slot $slot
```

# DirectShop Azure Deployment (Docker + Slots + CI/CD)

This repo now supports both:
- GitHub Actions workflows with Azure App Service deployment slots
- Azure DevOps `azure-pipelines.yml` with staging deployment and production swap

Yes, deployment slots are supported for Docker containers on Azure App Service.

## What was added
- `Dockerfile`
- `.dockerignore`
- `.github/workflows/deploy-azure-webapp-container.yml` (build + deploy to staging slot)
- `.github/workflows/promote-staging-to-production.yml` (manual swap)
- `.github/workflows/rollback-production-slot.yml` (manual rollback by tag)
- `azure-pipelines.yml` (Azure DevOps equivalent)
- `Server/Program.cs` uses `ConnectionStrings:DefaultConnection`
- `Server/appsettings.json` has default connection string

## 1) Prerequisites
- Azure subscription with credits
- Azure CLI installed and logged in
- GitHub repository (for GitHub Actions path)

## 2) Create Azure resources (PowerShell)
Use Standard tier or above for slots (`S1` recommended).

```powershell
$rg = "rg-directshop-prod"
$location = "uksouth"
$acr = "directshopacr12345"          # unique, lowercase
$plan = "asp-directshop-linux"
$app = "directshop-prod-12345"       # unique
$slot = "staging"

az group create --name $rg --location $location
az acr create --resource-group $rg --name $acr --sku Basic --admin-enabled true
az appservice plan create --resource-group $rg --name $plan --is-linux --sku S1

# Create web app with a bootstrap image (pipeline will overwrite)
az webapp create --resource-group $rg --plan $plan --name $app --deployment-container-image-name mcr.microsoft.com/dotnet/samples:aspnetapp

# Create staging slot
az webapp deployment slot create --resource-group $rg --name $app --slot $slot
```

## 3) Configure app settings (production + slot)

```powershell
az webapp config appsettings set --resource-group $rg --name $app --settings WEBSITES_PORT=8080 ASPNETCORE_FORWARDEDHEADERS_ENABLED=true ConnectionStrings__DefaultConnection="Data Source=/home/data/app.db"

az webapp config appsettings set --resource-group $rg --name $app --slot $slot --settings WEBSITES_PORT=8080 ASPNETCORE_FORWARDEDHEADERS_ENABLED=true ConnectionStrings__DefaultConnection="Data Source=/home/data/app.db"
```

Notes:
- `/home` is persistent on Linux App Service.
- SQLite is okay for low traffic/single-instance usage.
- For scale-out or heavy concurrency, move to Azure SQL.

## 4) Configure container settings once (production + slot)

```powershell
$acrLoginServer = "${acr}.azurecr.io"
$acrUser = az acr credential show --name $acr --query username -o tsv
$acrPass = az acr credential show --name $acr --query passwords[0].value -o tsv

az webapp config container set --resource-group $rg --name $app --docker-custom-image-name "$acrLoginServer/directshop:latest" --docker-registry-server-url "https://$acrLoginServer" --docker-registry-server-user $acrUser --docker-registry-server-password $acrPass

az webapp config container set --resource-group $rg --name $app --slot $slot --docker-custom-image-name "$acrLoginServer/directshop:latest" --docker-registry-server-url "https://$acrLoginServer" --docker-registry-server-user $acrUser --docker-registry-server-password $acrPass
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
az ad sp create-for-rbac --name "sp-directshop-gh" --role Contributor --scopes "/subscriptions/$subId/resourceGroups/$rg" --sdk-auth
```

Copy the JSON output into `AZURE_CREDENTIALS`.

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
az webapp restart --resource-group $rg --name $app --slot $slot
```

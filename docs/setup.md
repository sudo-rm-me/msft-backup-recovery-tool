# Deploying MBR

MBR runs in **Azure Container Apps** and is deployed with **Terraform**. The Terraform is a module,
`deploy/terraform/modules/mbr`, that you can run on its own from `deploy/terraform` (section 2) or call from your own Terraform
repository (section 3). The container is stateless.
Everything that matters lives outside it:

| What | Where |
|---|---|
| Metadata (tenants, snapshots index, schedules, plans, audit) | Azure Database for PostgreSQL Flexible Server, Microsoft Entra sign-in only (no passwords) |
| Master key that wraps every tenant's encryption key | Azure Key Vault (RSA key; never leaves the vault) |
| Certificates of the backup and restore apps | Azure Key Vault (generated there; the app downloads them at startup) |
| Backups | Your existing **Amazon S3** bucket, reached through AWS federation (no AWS keys) |
| Logs | Log Analytics |

The app signs in to all of them with one user-assigned managed identity.

## 1. What Terraform creates

- **In your Azure subscription:** the resource group, Container Registry, Key Vault (with purge protection), the master key and
  the two app certificates, PostgreSQL, the Container Apps environment and the app, Log Analytics, the managed identity, and the
  role assignments between them. It also builds the image from this repo in the registry (`az acr build`), unless you give
  it a prebuilt image (section 3).
- **In your Microsoft Entra tenant** (the operator's home tenant):
  - **MBR Backup**: multi-tenant, read-only application permissions. Every customer tenant consents to it.
  - **MBR Restore**: multi-tenant, write permissions. Customers consent only when they want restores (can be just in time).
  - **MBR Console**: operator sign-in for the web UI and CLI, pre-consented in your tenant.
  - **MBR AWS federation**: the audience of the token the app trades for AWS credentials. Only the app's identity can get one.

  The permissions come from `deploy/terraform/modules/mbr/permissions.json`, which a unit test keeps identical to the app's
  permission catalog.

Not created by Terraform: the IAM identity provider and role in AWS (your bucket's account, section 4), DNS for a custom
domain (section 6), and each customer tenant's admin consent (section 7).

## 2. Deploy on its own

You need Terraform 1.6+, the Azure CLI, and these roles:
- **In Azure:** *Owner* (or *Contributor* plus *Role Based Access Control Administrator*) on the subscription.
- **In Entra:** *Application Administrator* (or *Cloud Application Administrator*), plus *Privileged Role Administrator* to grant the console's consent.

```powershell
az login --tenant <your tenant ID>
cd deploy/terraform
Copy-Item terraform.tfvars.example terraform.tfvars   # fill in subscription, region, bucket, admins
terraform init
terraform apply -var "image_tag=$(git rev-parse --short HEAD)"
```

The first apply takes 10–15 minutes, most of it PostgreSQL. The app starts, but backups can't reach S3 until the IAM role
exists (section 4). For more than a trial, keep the state in Azure Storage: see `backend.tf.example`.

**Updating:** pull, then apply again with the new commit as `image_tag`. Terraform rebuilds the image and rolls out a new
revision, and database migrations run when it starts. Without a new tag, an apply changes configuration only.

## 3. Deploy from an existing Terraform repository

If you already manage Azure with Terraform, call the module from your own repository instead of running `deploy/terraform`.
Your repository keeps its own providers, backend, state and pipeline; the module brings only MBR's resources. The module has no
`provider` blocks, so it uses the providers you configure.

**1. Make the module reachable.** Pick one:

| Option | `source` in your module block | Notes |
|---|---|---|
| Git reference (recommended) | `git::https://github.com/sudo-rm-me/msft-backup-recovery-tool.git//deploy/terraform/modules/mbr?ref=<tag or commit>` | Terraform downloads the repository into `.terraform/modules` on `terraform init`. The repository is private, so whoever runs `init` (you or the pipeline) needs read access: a Git credential helper or token for HTTPS, or use `git::ssh://git@github.com/sudo-rm-me/msft-backup-recovery-tool.git//deploy/terraform/modules/mbr?ref=<tag>`. |
| Git submodule | `./vendor/mbr/deploy/terraform/modules/mbr` | `git submodule add https://github.com/sudo-rm-me/msft-backup-recovery-tool.git vendor/mbr`. Update with `git -C vendor/mbr checkout <tag>`. |
| Checkout next to your repository | `../msft-backup-recovery-tool/deploy/terraform/modules/mbr` | Simplest for one person; the path must be the same everywhere `terraform` runs. |
| Copy of the module folder | `./modules/mbr` (wherever you put it) | Copy `deploy/terraform/modules/mbr` into your repository. Use a prebuilt image (below), because the copy doesn't include the MBR source to build one from. |

**Where the image comes from.** By default (`image_tag`), the module builds the image from the whole MBR repository (its
`Dockerfile` and `src`, not only the module folder) with `az acr build`. With the first three options it finds the repository
on its own, four folders up from the module. Alternatively, set `image` to a **prebuilt image**: then nothing is built, and
neither the MBR source nor the Azure CLI is needed when you apply. That's the option for a copied module folder, and for
pipelines that shouldn't build images.

The simplest prebuilt image is the **published release image**. Every release (a `v1.2.3` tag) publishes one to GitHub
Container Registry, publicly, so nothing needs credentials:

```hcl
image = "ghcr.io/sudo-rm-me/msft-backup-recovery-tool:1.0.0"
```

Use the same version as the module (the module's `?ref=`, or the release you copied the folder from). The
[releases page](https://github.com/sudo-rm-me/msft-backup-recovery-tool/releases) lists them.

To use your own registry instead, build the image once per MBR version from a checkout of this repository, into any Azure
Container Registry:

```powershell
# from the MBR repository root, at the version you want
az acr build --registry <registry name> --image mbr:1.0.0 --file Dockerfile .
```

| Registry | Set in the module block |
|---|---|
| The published release image (public) | `image = "ghcr.io/sudo-rm-me/msft-backup-recovery-tool:1.0.0"` |
| This deployment's own registry (Terraform output `container_registry`, after the first apply) | `image = "<container_registry>/mbr:1.0.0"` |
| Another Azure Container Registry you have, e.g. a shared one | `image = "contosoacr.azurecr.io/mbr:1.0.0"` and `image_registry_id = "<that registry's resource ID>"`; the module gives the app's identity *AcrPull* on it |

The app signs in to Azure registries with its managed identity; private registries that need a user name and password
aren't supported. To update MBR, build the new version and change `image`; a new value rolls out a new revision. The first apply with
the deployment's own registry needs an image in it already, so for a brand-new deployment use another registry, or deploy once
with `image_tag` and switch to `image` afterwards.

**2. Providers.** Your root module needs these (most repositories already have the first two):

```hcl
terraform {
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "~> 4.20" }
    azuread = { source = "hashicorp/azuread", version = "~> 3.0" }
    random  = { source = "hashicorp/random", version = "~> 3.6" }
    time    = { source = "hashicorp/time", version = "~> 0.12" }
  }
}

provider "azurerm" {
  subscription_id = "<subscription for MBR>"
  features {
    key_vault {
      # Recommended: the vault holds the master key that decrypts every backup.
      purge_soft_delete_on_destroy       = false
      purge_soft_deleted_keys_on_destroy = false
      recover_soft_deleted_key_vaults    = true
    }
  }
}

provider "azuread" {} # the Entra tenant the apps are registered in (your home tenant)
```

If MBR goes in a different subscription from your default `azurerm` provider, add an aliased provider and pass it in with
`providers = { azurerm = azurerm.mbr }` in the module block.

**3. Add the module.** For example in `mbr.tf`:

```hcl
module "mbr" {
  source = "git::https://github.com/sudo-rm-me/msft-backup-recovery-tool.git//deploy/terraform/modules/mbr?ref=v1.0.0"

  location            = "westeurope"
  resource_group_name = "rg-platform-backup" # optional: an existing resource group
  name                = "mbr"
  image_tag           = "v1.0.0"             # keep in step with ?ref= so the image matches the module

  bootstrap_admins = ["you@contoso.com"]
  s3_bucket        = "contoso-mbr-backups"
  s3_region        = "eu-west-1"
  aws_account_id   = "123456789012"
  aws_role_arn     = "" # set after creating the IAM role (section 4)
}

# `terraform output` only shows your root module's outputs: pass through the ones you need.
output "mbr_url" { value = module.mbr.url }
output "mbr_next_steps" { value = module.mbr.next_steps }
output "mbr_aws_oidc_provider_url" { value = module.mbr.aws_oidc_provider_url }
output "mbr_aws_oidc_audience" { value = module.mbr.aws_oidc_audience }
output "mbr_aws_role_trust_policy" { value = module.mbr.aws_role_trust_policy }
output "mbr_aws_role_permissions_policy" { value = module.mbr.aws_role_permissions_policy }
```

`deploy/terraform/main.tf` is a complete example: it passes every variable through, and `deploy/terraform/outputs.tf` lists every
output.

**4. Apply.** Run `terraform init` (with `-upgrade` after changing `?ref=`), then plan and apply as you usually do. To update MBR,
change `?ref=` and `image_tag` together, or build a new prebuilt image and change `image`. Keep the module and the image at the
same MBR version: the module sets configuration that version of the app expects.

**What runs `apply` needs** (your machine or the pipeline):
- only when building the image (`image_tag`): the **Azure CLI**, signed in to the subscription, because the module runs
  `az acr build`. In a pipeline, sign in the CLI with the same service principal or workload identity Terraform uses (for example
  `azure/login` in GitHub Actions). With a prebuilt `image`, the CLI isn't needed;
- the Azure roles *Owner*, or *Contributor* plus *Role Based Access Control Administrator*, on the subscription or resource group;
- the Entra roles *Application Administrator* (or *Cloud Application Administrator*) and *Privileged Role Administrator*. For a
  service principal, the equivalent Microsoft Graph application permissions are `Application.ReadWrite.All` and
  `AppRoleAssignment.ReadWrite.All`.

### What to change for your repository

These are the settings that depend on where things are in your repository and environment. Everything else can keep its default.

| Setting | Where it's set | Standalone (`deploy/terraform`) | In your repository |
|---|---|---|---|
| Module `source` | Your module block | `./modules/mbr` | A Git reference, submodule path or relative path to `deploy/terraform/modules/mbr` (step 1). |
| `image` / `image_tag` | Module input | `-var image_tag=$(git rev-parse --short HEAD)` | Either `image_tag` (build here: the tag or commit you set in `?ref=`) or `image` (a prebuilt image, required if you copied only the module folder). Set one, not both. |
| `image_registry_id` | Module input | Not set | Only for a prebuilt image in a registry other than the deployment's own: that registry's resource ID. |
| `source_dir` | Module input | Not set: the repository the module is in | Leave unset. Only when building (`image_tag`) from a module that isn't inside an MBR checkout: the path to one (the folder with the `Dockerfile`), relative to where you run `terraform`. |
| `resource_group_name` | Module input | Not set: creates `rg-<name>` | An existing resource group, if you keep resources in your own groups. Resources go in `location` either way. |
| `name` | Module input | `mbr` | Must be unique in the resource group: it names the container app and is the start of the other resource names. |
| Subscription | Your `azurerm` provider | `subscription_id` variable | Your provider's `subscription_id`, or an aliased provider passed with `providers = { azurerm = azurerm.<alias> }`. `subscription_id` isn't a module input. |
| Entra tenant | Your `azuread` provider | The tenant `az login` uses | The tenant where operators sign in and the apps are registered. |
| State backend | Your repository | `backend.tf.example` | Your existing backend. MBR's state holds resource IDs and the certificates' public parts, not secrets. |
| Variable values | Your `.tfvars` or the module block | `terraform.tfvars` | `deploy/terraform/terraform.tfvars.example` lists them; set them as module inputs. |
| Outputs | Your `output` blocks | `terraform output <name>` | `module.mbr.<name>`, exposed with your own `output` blocks (step 3). Use those names in the AWS commands in section 4. |
| `permissions.json` | Inside the module | `modules/mbr/permissions.json` | Nothing to do: it travels with the module. |
| Resource group in `az` commands | Sections 6 and 11 | `-g rg-mbr -n mbr` | `-g <your resource group> -n <name>`. |

### Module inputs

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `location` | yes | | Azure region for all resources. |
| `s3_bucket` | yes | | Existing S3 bucket for backups. |
| `s3_region` | yes | | The bucket's AWS region. |
| `image_tag` or `image` | one of them | | `image_tag`: build the image from the MBR source with this tag. `image`: run this prebuilt image instead. |
| `image_registry_id` | | | Resource ID of the registry holding a prebuilt `image`, if it isn't the deployment's own; gets *AcrPull*. |
| `name` | | `mbr` | 3–12 lowercase letters and digits; the container app's name and the prefix of other resource names. |
| `resource_group_name` | | (new `rg-<name>`) | Existing resource group to deploy into. |
| `source_dir` | | (the module's repository) | Folder with MBR's `Dockerfile`, when building with `image_tag`. |
| `app_display_name_prefix` | | `MBR` | Prefix of the four Entra app registrations' names. |
| `bootstrap_admins` | | `[]` | UPNs that become global admins on first sign-in. Add more operators in the app afterwards. |
| `custom_domain` | | | Own host name for MBR (section 6). |
| `aws_role_arn` | | | IAM role the app assumes; set after section 4. |
| `aws_account_id` | | | Fills in the trust policy output. |
| `s3_prefix` | | | Folder inside the bucket. |
| `s3_object_lock_days`, `s3_object_lock_mode` | | `0`, `GOVERNANCE` | Object Lock retention on every object (the bucket must have Object Lock). |
| `s3_storage_class` | | | e.g. `STANDARD_IA`, `INTELLIGENT_TIERING`. |
| `cpu`, `memory` | | `2`, `4Gi` | Size of each replica (2 GiB per vCPU). |
| `min_replicas`, `max_replicas` | | `1`, `1` | Scale (section 9). `min_replicas` must be at least 1. |
| `postgres_sku`, `postgres_storage_mb`, `postgres_backup_retention_days` | | `B_Standard_B1ms`, `32768`, `14` | Database size and point-in-time restore window. |
| `certificate_generation`, `certificate_validity_months` | | `1`, `24` | App certificate renewal (section 11). |
| `notification_webhooks` | | `[]` | Alert webhooks. Can also be set in the app (section 5). |
| `partner_tenant_id` | | | Your own tenant ID, for GDAP (section 8). |
| `allowed_ip_ranges` | | `[]` | CIDR ranges allowed to reach the console and API (section 12). |
| `private_networking` | | `false` | Virtual network, private PostgreSQL and a Key Vault private endpoint (section 12). New deployments only. |
| `vnet_address_space` | | `10.60.0.0/22` | The virtual network's /22, with `private_networking`. |
| `management_ip_ranges` | | `[]` | With `private_networking`: where Terraform runs, so it can reach Key Vault. |
| `tags` | | `{}` | Tags on every resource. |

## 4. S3 access (AWS federation, no keys)

The app gets a token for **MBR AWS federation** from its managed identity and trades it for temporary credentials of an IAM role
(`AssumeRoleWithWebIdentity`). The role's trust policy allows only this deployment's identity. Set it up once in the AWS account
that owns the bucket (from your own repository, use your `output` names from section 3):

```powershell
terraform output -raw aws_role_trust_policy       > trust.json
terraform output -raw aws_role_permissions_policy > permissions.json

aws iam create-open-id-connect-provider --url (terraform output -raw aws_oidc_provider_url) `
    --client-id-list (terraform output -raw aws_oidc_audience)
aws iam create-role --role-name mbr-backup --assume-role-policy-document file://trust.json
aws iam put-role-policy --role-name mbr-backup --policy-name mbr-s3 --policy-document file://permissions.json
```

Set `aws_account_id` first, so the trust policy contains your account ID instead of a placeholder. Then set `aws_role_arn` to
the new role's ARN and apply again. The app updates its **S3 (deployment)** storage target from the Terraform values on every
start, until someone edits that target in **Settings → Storage**; from then on the app keeps its own version.

- **Ransomware protection:** use a bucket created with Object Lock, and set `s3_object_lock_days` (and `s3_object_lock_mode`). Objects
  can't be deleted or overwritten until retention expires, and `COMPLIANCE` mode can't be shortened, even by the root account.
  Keep MBR's retention at least as long as the lock.
- **Settings → Storage → Test** checks the bucket from the app: writing, reading, listing and deleting.
- More S3 targets (other buckets or S3-compatible stores) can be added in the UI and assigned per tenant.

## 5. Configuration

Terraform sets the configuration as environment variables on the container app (`__` separates levels, e.g.
`Mbr__Apps__Backup__ClientId`). Use the Terraform variables for the common ones:

| Variable | Meaning |
|---|---|
| `bootstrap_admins` | UPNs that become MBR global admins on first sign-in. |
| `s3_bucket`, `s3_region`, `s3_prefix`, `aws_role_arn` | The backup bucket and the IAM role (section 4). |
| `s3_object_lock_days`, `s3_object_lock_mode`, `s3_storage_class` | Object Lock and storage class for every object written. |
| `custom_domain` | Serve MBR on your own host name (section 6). |
| `cpu`, `memory`, `min_replicas`, `max_replicas` | Size and scale (section 9). |
| `image_tag` or `image` | Build the image from this repo, or run a prebuilt one (section 3). |
| `postgres_sku`, `postgres_backup_retention_days` | Database size and its point-in-time restore window. |
| `notification_webhooks` | URLs that get job-failure and drift alerts (JSON with a `text` field; works with Teams or Slack workflows). |
| `partner_tenant_id` | Import customers from GDAP (section 8). |

### Settings in the app

Global admins can change these in **Settings → General** without a Terraform apply. A value saved there is stored in the
database, overrides the deployment's value on every replica within a minute, and survives restarts and new revisions. **Use
deployment value** removes the override. Changes are in the audit log.

| Group | Settings |
|---|---|
| Alerts | Webhook URLs (stored encrypted); SMTP server, port, TLS, user name, password (stored encrypted), sender and recipients |
| GDAP | Partner tenant ID |
| Throughput | Jobs per replica, jobs per tenant, API requests per tenant, PowerShell timeout |

Storage targets, operators and per-tenant settings are managed in the app too. Settings that the infrastructure depends on
(database, Key Vault, app registrations and their certificates, sign-in, the public URL) come only from the deployment.

The app refuses to start in Container Apps if its database, master key or default storage would end up in the container.

## 6. Custom domain (optional)

Set `custom_domain = "mbr.contoso.com"` and apply. This changes the sign-in and consent addresses. Then point DNS at the app,
and bind a free managed certificate:

```powershell
$app = terraform output -raw default_host_name
# DNS: CNAME mbr.contoso.com → $app, and TXT asuid.mbr.contoso.com → the environment's verification ID:
az containerapp show -n mbr -g rg-mbr --query properties.customDomainVerificationId -o tsv
az containerapp hostname add  -n mbr -g rg-mbr --hostname mbr.contoso.com
az containerapp hostname bind -n mbr -g rg-mbr --hostname mbr.contoso.com --environment <environment name> --validation-method CNAME
```

## 7. Onboarding a customer tenant

1. **Tenants → Add tenant** (or `mbr tenant add contoso.onmicrosoft.com`).
2. Send the admin-consent links to a Global Admin of that tenant (or open them yourself if you are one).
3. For Azure, assign roles to the apps' service principals, ideally on the tenant root management group (or on each subscription):
   - backup app: *Reader* (management groups, resource groups, exported templates, RBAC and Policy);
   - restore app: *Contributor* (resource groups and template deployments), *User Access Administrator* (role assignments and
     custom roles) and *Resource Policy Contributor* (policy definitions, initiatives and assignments); *Management Group Contributor*
     on the root group if management groups should be restorable.
   Azure objects are restored into the subscription they came from; cross-tenant Azure restores aren't supported.
4. For the PowerShell-based workloads, assign Entra roles to both apps' service principals: *Exchange Administrator* (Exchange
   Online and Defender for Office 365), *Compliance Administrator* (Purview: labels, retention, DLP, alert policies) and
   *Teams Administrator* (Teams policies). The image includes PowerShell 7 with the `ExchangeOnlineManagement` and
   `MicrosoftTeams` modules, which sign in with the apps' certificates. Each module is read in one session per backup; a restore
   sends only the parameters that differ from the live object.
5. **Run permission check.** It shows granted and missing permissions per workload.

## 8. GDAP customers (partners)

If you manage customers through GDAP relationships, set `partner_tenant_id` to your own tenant ID. Terraform grants the backup
app two application permissions **in your own tenant only** (so set it in Terraform, not only in **Settings → General**): `DelegatedAdminRelationship.Read.All` (list relationships) and
`CrossTenantInformation.ReadBasic.All` (look up customers' names and domains). **Tenants → Import from GDAP** (or
`mbr partner customers` / `mbr partner import --all`) then adds customers in bulk. Each customer still grants admin consent to
the apps; where a relationship includes *Privileged Role Administrator* or *Global Administrator*, your own admins can do that.

## 9. Scaling

Every replica runs the API and a job worker. Jobs are claimed with leases in the database, and the scheduler runs on exactly one
replica at a time, so raise `max_replicas` to back up more tenants in parallel. Keep `min_replicas` at 1 or more: the
scheduler lives in the app. **Jobs per tenant** in **Settings → General** (default 2) caps how many jobs run at once for one
tenant across all replicas, which keeps each tenant within its Graph throttling budget. **Reports** shows the replicas that are online.

## 10. Microsoft 365 Backup

Tenants that pay for **Microsoft 365 Backup** (enabled in the Microsoft 365 admin center, billed through an Azure subscription)
show its policies, protected mailboxes / OneDrives / sites and restore points on the **Microsoft 365 Backup** page, and can run
Microsoft's native restores from there (or `mbr m365backup restore`): a whole mailbox, OneDrive or site to a point in time, either
to a new location or in place. It complements this tool's item-level backups and doesn't replace them. The backup app has
`BackupRestore-Configuration.Read.All` and `BackupRestore-Monitor.Read.All`, and the restore app has
`BackupRestore-Restore.ReadWrite.All`. Tenants without the service just show that it isn't set up.

## 11. Operations

- **Logs:** Log Analytics (`ContainerAppConsoleLogs_CL`), or `az containerapp logs show -n mbr -g rg-mbr --follow`.
- **Health:** `/healthz` (the app is up and its database answers); Container Apps restarts replicas that fail it.
- **Certificate renewal:** the app certificates are valid for `certificate_validity_months` (Key Vault emails its contacts 30
  days before expiry). To renew both, increase `certificate_generation` by one and apply. Terraform creates new certificates in
  Key Vault, registers them on the apps and rolls out a revision that uses them, then removes the old ones.
- **Disaster recovery:** see [disaster-recovery.md](disaster-recovery.md). In short: backups are readable only with the Key Vault
  master key, so keep an offline backup of it; PostgreSQL keeps point-in-time backups for `postgres_backup_retention_days`; and
  Terraform refuses to destroy the vault, the master key or the database (`prevent_destroy`).
- **Network:** by default the console is reachable from the internet (sign-in required) and PostgreSQL accepts connections from
  Azure services, because Consumption Container Apps have no fixed outbound IPs. Only Microsoft Entra identities can sign in to
  PostgreSQL, and the app's identity is its only administrator. Section 12 locks both down.

## 12. Network lock-down (optional)

**Restrict who can reach the console.** Set `allowed_ip_ranges` to your offices' and VPN's public ranges, e.g.
`["203.0.113.0/24"]`. Everyone else gets a 403 from Container Apps before reaching MBR. Two things go through the browser
of someone outside your network, so plan for them:
- **Admin consent:** after a customer's admin consents, Microsoft sends their browser back to MBR's consent callback. If their IP
  isn't allowed, the consent is still granted (it happens at Microsoft); the callback just can't record it. Run **Permission
  check** on the tenant afterwards, and MBR picks it up.
- **The CLI:** run it from an allowed network.

**Private networking.** `private_networking = true` puts the Container Apps environment in a new virtual network
(`vnet_address_space`, a /22), makes PostgreSQL reachable only inside it (no public endpoint), and gives Key Vault a private
endpoint for the app. Key Vault's public endpoint then admits only `management_ip_ranges`: set it to the public IP of wherever
Terraform runs, because Terraform manages the master key and the certificates through it.

```hcl
private_networking   = true
management_ip_ranges = ["198.51.100.7"] # your pipeline's or machine's public IP
allowed_ip_ranges    = ["203.0.113.0/24"]
```

Set it on the **first** apply: PostgreSQL's network mode can't change once the server exists, and Terraform refuses to replace
the server (`prevent_destroy`). To move an existing deployment, deploy a new one with private networking and restore the
database into it (see [disaster-recovery.md](disaster-recovery.md)). The app still reaches S3, Microsoft Graph and ARM over the
internet, and the image is still pulled from its registry as before.

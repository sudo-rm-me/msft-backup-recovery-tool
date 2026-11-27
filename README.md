# Microsoft Tenant Backup & Recovery (MBR)

Back up and restore Microsoft 365 and Azure tenants: Entra ID, Intune, Exchange Online, SharePoint/OneDrive,
Teams, Defender, Purview and Azure resource configuration, plus mailbox and file data. It is built for MSPs and
admins who manage **many tenants** from one deployment, with a web console and a CLI.

- **Multi-tenant**: onboard customer tenants with admin consent; choose the tenant in the UI sidebar or with `--tenant`.
- **Storage**: **Amazon S3** (or any S3-compatible store), reached from Azure through federation with no AWS keys.
  Everything is encrypted per tenant (AES-256-GCM, with keys wrapped by a Key Vault master key) before it leaves the app.
  S3 Object Lock is supported.
- **Runs in Azure Container Apps**, deployed with Terraform: the app, its Entra app registrations, Key Vault, PostgreSQL and
  the image build.
- **Scheduling**: cron schedules (with presets) per tenant, with time zones and overlap protection.
- **Safe restores**: plan → (optional approval) → apply → verify, with ID remapping and dependency ordering.

See [PLAN.md](PLAN.md) for the roadmap and design, [docs/setup.md](docs/setup.md) to deploy it, and
[docs/disaster-recovery.md](docs/disaster-recovery.md) for recovering it. Security reports: [SECURITY.md](SECURITY.md).

## Quick start (local development)

Requirements: .NET 10 SDK, Node 20+.

```powershell
cd src/web; npm install; npm run build; cd ../..
dotnet run --project src/Mbr.Api
```

Open http://localhost:5180. In Development mode you're signed in automatically as a local global admin, the database
is SQLite and backups go to `%LOCALAPPDATA%\Mbr\storage`. To try real tenants locally, see docs/development.md.

For UI development with hot reload, run the API as above and `npm run dev` in `src/web` (http://localhost:5173).

## Deploying

```powershell
cd deploy/terraform
terraform init
terraform apply -var "image_tag=$(git rev-parse --short HEAD)"
```

To deploy from your own Terraform repository instead, call the module `deploy/terraform/modules/mbr` from it.
See [docs/setup.md](docs/setup.md): it covers both ways, the variables, the one-time AWS role for the S3 bucket, custom domains
and updates. Alerts, GDAP and throughput settings can also be changed later in the app (Settings → General).

## CLI

```powershell
dotnet run --project src/Mbr.Cli -- login --server https://mbr.contoso.com
dotnet run --project src/Mbr.Cli -- tenant add contoso.onmicrosoft.com --name Contoso
dotnet run --project src/Mbr.Cli -- schedule add -t contoso --name nightly --job-type backup.config --cron daily
dotnet run --project src/Mbr.Cli -- job list -t contoso
dotnet run --project src/Mbr.Cli -- diff -t contoso --from baseline --to live -d
dotnet run --project src/Mbr.Cli -- restore plan -t contoso --snapshot latest --type intune.deviceConfiguration
dotnet run --project src/Mbr.Cli -- restore apply <plan-id>
dotnet run --project src/Mbr.Cli -- entra deleted -t contoso --kind user
dotnet run --project src/Mbr.Cli -- data backup -t contoso
dotnet run --project src/Mbr.Cli -- data ls -t contoso adele@contoso.com /Documents --at 2026-11-20T09:00
dotnet run --project src/Mbr.Cli -- data restore -t contoso adele@contoso.com /Inbox/Projects --conflict skip
dotnet run --project src/Mbr.Cli -- azure rgs -t contoso --deleted
dotnet run --project src/Mbr.Cli -- azure redeploy -t contoso rg-web-prod --apply
dotnet run --project src/Mbr.Cli -- report --csv protection.csv
dotnet run --project src/Mbr.Cli -- partner import --all
dotnet run --project src/Mbr.Cli -- m365backup units -t contoso
dotnet run --project src/Mbr.Cli -- m365backup restore -t contoso adele@contoso.com
```

Set `MBR_TENANT` to avoid repeating `--tenant`. Every command accepts `--json`.

## Layout

| Path | What |
|---|---|
| `src/Mbr.Core` | Domain model, abstractions, permission catalog |
| `src/Mbr.Catalog` | Resource handlers (what gets backed up and how it's restored) |
| `src/Mbr.Infrastructure` | Database, Graph/ARM/PowerShell clients, S3 storage, encryption, jobs, scheduler, in-app settings |
| `src/Mbr.Api` | REST API; serves the web UI and runs the job worker and scheduler |
| `src/Mbr.Cli` | `mbr` command-line tool |
| `src/web` | React + Fluent UI console |
| `tests/Mbr.Tests` | Unit and integration tests (fake Graph/ARM, no tenant needed) |
| `deploy/terraform` | Azure Container Apps deployment: the `modules/mbr` module (infrastructure, Entra app registrations, image build) and a standalone wrapper |

## Tests

```powershell
dotnet test
```

Tests run against in-memory fakes of Microsoft Graph, ARM and the PowerShell modules, so no tenant is needed. For checking
against a real test tenant, see docs/testing.md.

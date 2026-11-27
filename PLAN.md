# Microsoft Tenant Backup & Recovery Tool — Plan

## Goals

- Back up and restore **configuration and data** across Microsoft cloud services: Entra ID, Intune, Exchange Online, SharePoint/OneDrive, Teams, Defender, Purview, and Azure.
- **Multi-tenant**: one deployment manages many customer tenants. The tenant can be selected in both the UI and the CLI.
- **Web UI from day one**, plus a CLI that uses the same API (for scripting and automation).
- Restores are **safe by default**: every restore is shown as a plan first (dry run), then applied, and every action is recorded in an audit log.

## Non-goals (for now)

- Replacing Azure Backup for VM disks and managed databases. Azure resource *definitions* are in scope. Azure resource *data* is left to Azure Backup and noted as a later integration.
- Backup of Teams chat and channel messages (removed from scope: protected, metered APIs, and messages can't be restored as their authors).

---

## Technology stack

| Layer | Choice | Why |
|---|---|---|
| Backend API | **ASP.NET Core (.NET 10 LTS)** | First-class Microsoft Graph and Azure SDKs; strong typing for hundreds of resource schemas; good performance for long-running bulk jobs |
| Jobs | A job worker and scheduler inside the API process | Every replica takes jobs from the shared queue, so adding replicas scales out backups |
| PowerShell-only workloads | **Out-of-process `pwsh` runner** (script in, JSON out) | Exchange, Teams and Security & Compliance settings are only reachable through their PowerShell modules. Running them in a child `pwsh` process avoids assembly conflicts between those modules and the host |
| Frontend | **React + TypeScript + Vite + Fluent UI v9** | Fluent gives the familiar Microsoft admin-portal look; React has the best ecosystem for large data tables, diff viewers and tree views |
| CLI | .NET console app (`System.CommandLine`) calling the REST API | Shares the client library with the UI's API surface; uses `--tenant` on every command |
| Metadata DB | **PostgreSQL** in production, **SQLite** for local dev (EF Core) | Tenants, jobs, snapshot indexes, ID maps, audit log, operator accounts and permissions |
| Job queue + scheduler | Built-in DB-backed queue with lease-based claiming, plus a cron scheduler (Cronos) | Works on both SQLite and PostgreSQL; schedules, retries, progress reporting, cancellation; every replica shares one queue, and the scheduler runs on one replica at a time |
| Snapshot/data store | **Amazon S3** (or any S3-compatible store), reached from Azure through federation (the app's managed identity assumes an IAM role; no AWS keys). Each tenant can have its own target. Local folders are for development only | Keeping backups outside Microsoft's cloud limits the blast radius; S3 Object Lock gives WORM protection |
| Secrets | **Azure Key Vault** | App certificates and the master key that wraps each tenant's data-encryption key |
| Hosting | **Azure Container Apps**, deployed with **Terraform** (infrastructure, Entra app registrations, image build). The container is stateless: PostgreSQL, Key Vault and S3 hold everything. Local dev uses SQLite + a local folder | The operator runs it in their own Azure subscription |

---

## Multi-tenant model

**Operator side (the tool's "home" tenant)**
- Operators sign in to the UI and CLI with Entra ID (OIDC) in the home tenant.
- Permissions inside the tool, scoped **per customer tenant**:
  - `Viewer`: browse snapshots and reports
  - `BackupOperator`: run and schedule backups
  - `RestoreOperator`: plan and apply restores
  - `Admin`: onboard tenants and manage settings
- Applying a restore can optionally require **two-person approval** (one operator creates the plan, another approves it).

**Customer side (each managed tenant)**
- Two **multi-tenant app registrations** owned by the home tenant:
  - **Backup app**: read-only Graph application permissions.
  - **Restore app**: write permissions. It is consented only when a tenant opts in, and can be consented "just in time".
- Onboarding flow in the UI:
  1. Enter the tenant domain.
  2. A Global Admin in that tenant completes the admin-consent link.
  3. The tool runs a permission check that shows what's granted and what's missing.
  4. Optional extra steps the tool walks the admin through, because consent alone doesn't cover them:
     - **Azure**: assign Reader (backup) or Contributor (restore) to the service principal on each subscription or management group.
     - **Exchange app-only**: grant `Exchange.ManageAsApp` and assign the Exchange Administrator role to the service principal.
- MSPs that use partner relationships can import customers from **GDAP**; each customer still consents to the apps.
- Authentication is app-only with **certificates** stored in Key Vault. No client secrets.

**Isolation**
- Each tenant's data lives under its own prefix (`tenants/{tenantId}/`) in its storage target, and tenants can use different targets.
- A separate data-encryption key per tenant, wrapped by a Key Vault key (envelope encryption).
- Every DB row is keyed by `TenantId`, and every query is filtered by tenant at the data-access layer.

---

## Architecture

```
 ┌──────────────┐   ┌──────────────┐
 │  React UI    │   │     CLI      │
 └──────┬───────┘   └──────┬───────┘
        └──────── REST API (ASP.NET Core) ────────┐
                         │                         │
                 Job queue (Postgres)        Audit log
                         │
                ┌────────┴──────────────────┐
                │ Job worker (every replica) │
                └────────┬──────────────────┘
   ┌────────────┬────────┴───────┬──────────────┐
 Backup      Diff/Drift       Restore        Retention
 engine       engine     (plan → approve →   & pruning
                          apply, ID remap)
                         │
              Resource catalog (handlers)
   config: entra.* intune.* azure.* exo.* teams.* spo.* defender.* purview.*
   data:   mail.* files.*
                         │
     API clients: Graph · ARM · EXO/Teams/SPO PowerShell
     (throttling, Retry-After, paging, $batch, delta tokens)
                         │
       Storage abstraction: S3 (Object Lock, encrypted) / local FS (dev)
```

### Resource catalog (core abstraction)

Each resource type is a handler with this shape:

```csharp
interface IResourceHandler {
    string Type { get; }                       // e.g. "intune.deviceConfiguration"
    Workload Workload { get; }
    IEnumerable<string> DependsOnTypes { get; } // used to order restores
    IAsyncEnumerable<ResourceItem> ListAsync(TenantContext ctx, DeltaState? since);
    JsonNode Normalize(JsonNode raw);           // strip read-only/volatile fields
    IEnumerable<Reference> ExtractReferences(JsonNode item); // object IDs it points at
    Task<RestoreResult> CreateAsync(...);
    Task<RestoreResult> UpdateAsync(...);
    Task DeleteAsync(...);
    IEnumerable<string> SecretFields { get; }   // flagged "re-enter manually"
}
```

- Most Graph configuration types can be **declarative**: endpoint, field rules and reference paths in a JSON or YAML catalog, run by one generic Graph handler. Only unusual types need custom code. Adding coverage is mostly adding catalog entries.
- Handlers also declare *child* resources, such as Intune assignments and settings-catalog settings, which are backed up and restored together with their parent.

### Storage layout

```
tenants/{tenantId}/objects/{hh}/{sha256}   encrypted, content-addressed objects (config items, file chunks, messages)
tenants/{tenantId}/docs/{name}             encrypted named documents (snapshot manifests, indexes)
```
- A snapshot manifest lists the tenant, time, tool and schema version, each item's hash, counts and errors.
- Each config object is normalized and stably ordered JSON, so snapshots diff cleanly.
- Content-addressed deduplication across snapshots: an unchanged object is stored once and referenced by hash. This makes frequent snapshots cheap.

### Data backup design

| Workload | Backup method | Restore method |
|---|---|---|
| **OneDrive / SharePoint files** | Graph `driveItem` **delta** queries per drive; files stored content-addressed (SHA-256) with chunking for large files; versions kept per retention policy | Upload back to the original or an alternate location, also in another tenant; conflict modes are skip, rename or overwrite; permissions re-applied where possible |
| **Exchange mailboxes** | Graph per-folder message delta, exporting full MIME or FTS items (Graph mailbox export/import APIs where available); calendars and contacts through their Graph endpoints | Import into the original or an alternate mailbox or folder, preserving read/sent flags via import APIs or extended properties |
| **Teams** | Team/channel structure and settings count as config. Channel files live in SharePoint and use the file pipeline. Chat and channel *messages* are **out of scope**: the Graph export APIs are protected and metered (paid per call) | Graph cannot re-post messages as the original author, so messages couldn't be truly restored anyway |
| **Microsoft 365 Backup** | For tenants that license it: show its policies, protected units and restore points | Native restores through its APIs, alongside this tool's item-level restores |

Data jobs are **incremental by default**: delta tokens are stored per drive or folder, and job checkpoints let an interrupted multi-day initial backup resume instead of restarting.

### Restore engine

1. **Select**: one object, a set of objects, a workload, or a whole snapshot. The target is the same tenant or a different tenant ("clone or migrate").
2. **Plan**: compare the snapshot with the live state and resolve every reference through the **ID map**: original ID → current ID, matched by ID, then by stable keys such as UPN or display name plus type. For a different tenant, an optional **domain map** (old → new domain, plus the `.onmicrosoft.com` domains automatically) is applied to user names and addresses first. Produce an ordered list of create, update, skip and delete actions using the dependency graph. Show warnings for redacted secrets, missing licenses, and references that can't be resolved.
3. **Approve**: optional second-operator approval.
4. **Apply**: run in dependency order with per-item results, then record new IDs back into the ID map.
5. **Verify**: re-read the restored items and compare them against the snapshot.

**Entra-specific rules**
- Check the recycle bin first (`directory/deletedItems`, kept for 30 days). Restoring from it keeps the original object ID and its references.
- Objects that were already purged are recreated with new IDs, and everything that referenced them is re-linked through the ID map: group memberships, Conditional Access, Intune assignments, roles, app role assignments.

---

## UI (day-one screens)

1. **Tenants**: list, onboarding wizard, permission health check, tenant switcher in the sidebar.
2. **Dashboard** (per tenant): last backup status, coverage by workload, storage used, drift alerts.
3. **Backups**: run now, schedules (cron expressions with presets such as hourly, daily or weekly, in a chosen time zone), policies (which workloads, which storage target, retention).
4. **Jobs**: live progress, logs, per-item errors, retry and cancel.
5. **Snapshot explorer**: browse by workload and type, view an object's JSON, see its history over time.
6. **Diff**: snapshot vs. snapshot, or snapshot vs. live, with a side-by-side JSON diff.
7. **Restore**: wizard covering selection, target, plan review, approval, apply and verify.
8. **Data browser**: mailbox folders and messages, and drive file trees, with search and restore or download.
9. **Audit log** and **Settings** (storage, deployment-wide settings such as alerts and throughput, operators, app registrations).

## CLI (mirrors the API)

```
mbr login
mbr tenant list | add | check --tenant contoso.onmicrosoft.com
mbr backup run --tenant contoso --workload intune,entra
mbr snapshot list --tenant contoso
mbr diff --tenant contoso --from <snap> --to live
mbr restore plan --tenant contoso --snapshot <snap> --type entra.conditionalAccessPolicy --out plan.json
mbr restore apply --plan plan.json
mbr job watch <jobId>
```

---

## Repository layout

```
/src
  Mbr.Api/              ASP.NET Core REST API + auth; serves the web UI and runs the job worker and scheduler
  Mbr.Core/             domain model, abstractions, permission catalog
  Mbr.Catalog/          resource handlers + declarative catalog (catalog/*.yaml)
  Mbr.Infrastructure/   database (EF Core + migrations), Graph/ARM/PowerShell clients, storage, encryption,
                        jobs, backup, restore, diff, settings, demo tenant
  Mbr.Cli/              CLI
  web/                  React + Fluent UI app
/tests
  Mbr.Tests/            unit and integration tests against in-memory fakes of Graph, ARM and PowerShell
/deploy/terraform       Azure Container Apps deployment: modules/mbr (the module) + a standalone wrapper
/docs                   setup, development, testing
```

---

## Phased roadmap

Each phase ends with a usable feature in **both UI and CLI**.

Progress: phases 0–9 are done (see the git history for each phase's commit).

| # | Phase | Scope | Exit criteria |
|---|---|---|---|
| 0 | **Foundations** | Solution skeleton; operator sign-in; tenant registry and onboarding wizard (consent + permission check); job queue; storage abstraction with encryption; UI shell with tenant switcher and Jobs page; Graph client with throttling | You can onboard the test tenant and run a no-op job you can watch live |
| 1 | **Config backup: Entra + Intune** | Conditional Access, named locations, authentication methods and policies, groups, users (metadata), directory roles + PIM, app registrations and service principals (metadata); Intune device configs, Settings Catalog, compliance, app protection/configuration, scripts and remediations, filters, scope tags, enrollment profiles, assignments. Schedules + retention | Scheduled backups run; the snapshot explorer shows every item |
| 2 | **Diff and drift** | Snapshot vs. snapshot and vs. live; drift alerts (email or webhook) | A change made in the portal shows up as drift |
| 3 | **Config restore: Intune + Conditional Access** | Planner, ID map, dependency ordering, dry run, approval, apply, verify; cross-tenant clone | Delete a policy in the test tenant and restore it with its assignments intact |
| 4 | **Entra object restore** | Recycle bin restore, recreation of purged objects, re-linking memberships, roles and assignments | Purge a group, restore it, and all its references are re-linked |
| 5 | **Data backup: OneDrive/SharePoint** | Delta-based file backup, dedup, versions, resume; data browser | Incremental backups of all drives complete within the throttling limits |
| 6 | **Data backup + restore: Exchange** | Mail, calendar and contacts backup; restore to original or alternate mailbox; file restore | Restore a deleted folder of mail and a deleted SharePoint folder |
| 7 | **Azure config** | Management groups, subscriptions, resource groups (ARM/Bicep export), RBAC, Azure Policy and initiatives, plus restore through deployments | Redeploy a deleted resource group's resources from a snapshot |
| 8 | **More config workloads** | Exchange Online settings (transport rules, connectors, policies), Teams policies and settings, SharePoint tenant settings, Defender for Office/Endpoint policies, Purview (DLP, retention, labels) | Each workload has backup + diff; restore where the APIs allow it |
| 9 | **Advanced** | Microsoft 365 Backup integration, GDAP, reporting/exports, scaling out across replicas | — |

---

## Cross-cutting concerns

- **Throttling**: per-tenant and per-workload concurrency limits, Retry-After handling, `$batch` (20 requests per call), delta queries, adaptive backoff. Large tenants will be limited by throttling, not by the tool, so measure early.
- **Resumability**: every job checkpoints, and workers can crash without losing progress.
- **Security**: least-privilege permissions documented per workload; backup and restore apps kept separate; S3 Object Lock; per-tenant encryption keys; audit log of every operator action; no secrets in logs.
- **Gaps are reported**: each snapshot manifest lists what could *not* be captured (redacted secrets, unsupported types, permission errors). Coverage is never silently incomplete.
- **Schema drift**: Microsoft changes Graph (especially beta). The API version is stored per item. Live tests against a test tenant are a manual checklist for now (docs/testing.md).
- **Testing**: the full API, job runner and planner run against in-memory fakes of Graph, ARM and PowerShell (docs/testing.md).

## Permissions

The permission catalog (`Mbr.Core/Permissions`) lists every permission with the workload and reason. Terraform requests exactly
those (`deploy/terraform/modules/mbr/permissions.json`, kept identical by a unit test), and **Settings → App registrations**
shows them. The backup app only reads; the restore app holds the write permissions and is consented separately.

## Decisions

1. **Hosting**: each operator self-hosts in their own Azure subscription, on Azure Container Apps, deployed with Terraform.
2. **Job queue**: a built-in DB queue rather than Hangfire, whose SQLite support is community-maintained.
3. **Backups outside Microsoft's cloud**: Amazon S3 through federation, with no AWS keys.

## Open questions

1. **Retention defaults**: for example, daily config snapshots kept for 90 days; data versions kept for 30 days plus monthly copies for 1 year.
2. **Licensing posture for data backup**: how to handle Graph APIs that are metered or protected (some mailbox export APIs).

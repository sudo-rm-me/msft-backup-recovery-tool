# Testing

## Automated tests (no tenant needed)

```powershell
dotnet test
```

The suite in `tests/Mbr.Tests` runs the real API, job runner, planner and storage against in-memory fakes of Microsoft Graph,
ARM and the Exchange / Security & Compliance / Teams PowerShell modules (`src/Mbr.Infrastructure/Demo`, `tests/Mbr.Tests/Fakes`).
Each test builds its own app with a temporary SQLite database and local storage.

| File | Covers |
|---|---|
| `FoundationTests` | Tenants, onboarding, permission checks, jobs, storage, encryption and the Terraform deployment (permissions, module wiring) |
| `AppSettingsTests` | Settings changed in the app: overrides, reset, encrypted secrets, validation, the deployment storage target |
| `ConfigBackupTests`, `DiffTests` | Config snapshots, schedules, retention, diff and drift alerts |
| `RestoreTests`, `EntraRestoreTests` | Restore planning, ordering, ID mapping, approval, verification, recycle bin and recreation |
| `StorageImportTests` | Losing the database: rebuilding tenants, snapshots and file versions from storage with the master key |
| `CrossTenantTests` | Restoring into another tenant: domain mapping, users, and mail into the same user there |
| `FileBackupTests`, `MailAndRestoreTests` | OneDrive/SharePoint and mailbox backup, versions, resume and item restore |
| `AzureTests`, `WorkloadTests` | Azure (ARM templates, RBAC, Policy) and the PowerShell/Graph workloads |
| `ReportingTests`, `AdvancedTests` | Reports, replicas sharing the job queue, GDAP and Microsoft 365 Backup |

The fakes model the behavior the code depends on (paging, delta tokens, throttling, long-running operations), not every
detail of the real services, so a check against a real tenant is still needed before relying on a workload.

## Live smoke tests (test tenant)

`tests/Mbr.LiveTests` runs the real app (temporary database and local storage) against a **test tenant**. It isn't in
`Mbr.sln`, so `dotnet test` doesn't run it. It checks that both apps sign in and the backup app has every required permission,
that an Entra and Intune config backup succeeds, and a round trip: it creates a group named `mbr-smoke-*`, backs it up,
deletes it, restores it from the recycle bin, then deletes and purges it.

Set the tenant and the two apps (their certificates must be consented in that tenant), then run it:

```powershell
$env:MBR_LIVE_TENANT = "contoso-test.onmicrosoft.com"
$env:Mbr__Apps__Backup__ClientId = "<backup app id>"
$env:Mbr__Apps__Backup__CertificatePath = "C:\certs\backup.pfx"
$env:Mbr__Apps__Restore__ClientId = "<restore app id>"
$env:Mbr__Apps__Restore__CertificatePath = "C:\certs\restore.pfx"
dotnet test tests/Mbr.LiveTests
```

(`Mbr__Apps__*__CertificatePassword` if the PFX has one.) Without `MBR_LIVE_TENANT` every test fails with a message saying what
to set.

On GitHub, **Actions → Live smoke tests → Run workflow** does the same. It runs in the `live-tests` environment (add required
reviewers there if you like) and needs these secrets: `MBR_LIVE_TENANT`, `MBR_LIVE_BACKUP_CLIENT_ID`,
`MBR_LIVE_RESTORE_CLIENT_ID`, `MBR_LIVE_BACKUP_PFX_BASE64`, `MBR_LIVE_RESTORE_PFX_BASE64` (the PFX files, base64) and
`MBR_LIVE_PFX_PASSWORD` (may be empty).

## Checking against a test tenant by hand

The smoke tests cover the basics. Before relying on a workload, also run this checklist against a **test tenant** (never
production) after deploying with `docs/setup.md`. Use only test data: restores create, change and delete objects.

1. **Onboard:** add the tenant, grant consent, and check that **Permissions** shows nothing missing (or only the opt-in ones).
2. **Config backup:** run a backup with every workload and open the snapshot. Each workload should have items and no errors.
3. **Drift:** change a Conditional Access policy in the portal, run drift, and check that the change shows.
4. **Config restore:** delete an Intune policy that has assignments, then restore it from the snapshot. It should come back with its
   assignments, and verification should pass.
5. **Entra:** delete a group (soft delete), restore it from the recycle bin, then purge another group and recreate it. Its memberships
   and role assignments should be re-linked.
6. **Files and mail:** back up one OneDrive, one site and one mailbox. Change and delete some items, back up again, then restore a
   deleted folder and an older version of a file.
7. **Azure:** in a test subscription, delete a small resource group and redeploy it from the snapshot.
8. **Optional:** if the tenant has Microsoft 365 Backup, restore a mailbox to a new location.

Note anything that differs from the fakes; each difference should become a fix plus a regression test that uses the fake.

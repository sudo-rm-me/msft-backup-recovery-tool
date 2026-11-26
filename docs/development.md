# Development

## Running locally

```powershell
cd src/web; npm install; npm run build; cd ../..
dotnet run --project src/Mbr.Api          # http://localhost:5180
```

`appsettings.Development.json` turns on:

- **Development auth**: you're signed in as a local global admin.
- **Demo mode** (`Mbr:Demo:Enabled`): adds a simulated tenant, *Contoso (demo)*, whose Microsoft Graph and ARM calls go to an
  in-memory fake (`src/Mbr.Infrastructure/Demo`). Every page works without a real tenant. The simulated data resets on restart.

UI hot reload: keep the API running and run `npm run dev` in `src/web` (http://localhost:5173 proxies `/api`).

## Real tenants locally

To run a local build against real tenants, reuse the deployment's app registrations. Sign in with `az login` as someone who
can read the deployment's Key Vault (whoever ran Terraform can), then set:

```powershell
$env:Mbr__Apps__Backup__ClientId = terraform -chdir=deploy/terraform output -raw backup_app_client_id
$env:Mbr__Apps__Backup__KeyVaultCertificate = "https://<key vault>.vault.azure.net/secrets/mbr-backup-1"
# and the same for Restore, then: dotnet run --project src/Mbr.Api
```

The certificate is downloaded with your Azure CLI sign-in. Locally, backups go to a local folder; the S3 federation only works
for the deployed app's managed identity.

## Database migrations

The metadata database has one EF Core migration set per provider. After changing entities in `Mbr.Core/Model` or
`MbrDbContext`, add a migration to **both**:

```powershell
dotnet tool restore
dotnet ef migrations add <Name> --project src/Mbr.Infrastructure --context SqliteMbrDbContext --output-dir Data/Migrations/Sqlite
dotnet ef migrations add <Name> --project src/Mbr.Infrastructure --context PostgresMbrDbContext --output-dir Data/Migrations/Postgres
```

Migrations are applied automatically at startup.

## Adding a resource type

Most Graph resources need only a catalog entry in `src/Mbr.Catalog/catalog/<workload>.yaml`:

```yaml
- type: intune.something            # unique key
  workload: intune
  displayName: Something
  version: beta                     # v1.0 (default) or beta
  listPath: deviceManagement/somethings
  getItem: true                     # re-read each item if the list omits properties
  children:                         # sub-collections embedded in the item
    - name: assignments
      path: deviceManagement/somethings/{id}/assignments
      strip: [id]
      restore: { mode: assign }     # inline | assign | each | ref | none
  references:                       # IDs remapped on restore
    - { path: "assignments[*].target.groupId", type: entra.group }
  dependsOn: [entra.group]          # restored first
  strip: [someReadOnlyProperty]
  secretPaths: [password]           # not returned by Graph; flagged on restore
```

Resources that don't fit (PowerShell-only, ARM, unusual restore semantics) implement `IResourceHandler` in
`Mbr.Catalog/Handlers` and are registered in `FeatureRegistration`.

## Tests

`dotnet test` runs everything against the in-memory Graph/ARM fake. `TestApp` hosts the full API with a temp
SQLite database and local storage; `FakeTenantData` seeds a realistic tenant.

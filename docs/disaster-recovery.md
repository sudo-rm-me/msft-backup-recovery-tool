# Disaster recovery

MBR's backups are only useful if MBR itself can be recovered. This runbook covers what to protect, how to keep an offline
copy of the one thing that can't be recreated, and how to rebuild a deployment.

## What matters and how it's protected

| Piece | If it's lost | Protection |
|---|---|---|
| **Key Vault master key** (`mbr-master`) | **Every backup becomes unreadable.** Each tenant's data key is wrapped by it. | Purge protection (deleted keys and vaults are recoverable for 90 days), `prevent_destroy` in Terraform, and an offline backup (below). |
| PostgreSQL (metadata) | MBR forgets its tenants, snapshots, schedules and settings. The backup data in S3 is intact and still decryptable: each tenant's data key is also stored, wrapped by the master key, in `tenants/<tenant id>/key.json` next to its backups. | Point-in-time restore for `postgres_backup_retention_days`, `prevent_destroy`. |
| S3 bucket (backup data) | The backups themselves. | S3 versioning and Object Lock (`s3_object_lock_days`); optionally replication to a second region. |
| App certificates | Can be recreated: bump `certificate_generation` and apply. | Key Vault. |
| Entra app registrations | Can be recreated by Terraform, but customers then need to consent again (new app IDs). | Terraform state; don't delete them by hand. |
| The container app, registry, environment | Recreated by `terraform apply`. | Stateless. |

## 1. Keep an offline backup of the master key

Do this after the first apply, and again whenever the key gets a new version (the app pins one version: see
`Mbr__Encryption__KeyVaultKeyId`). You need *Key Vault Administrator* on the vault.

```powershell
$kv = terraform output -raw key_vault_name
az keyvault key backup --vault-name $kv --name mbr-master --file mbr-master.keybackup
```

The file is encrypted by Azure and can only be restored into a Key Vault **in the same Azure geography and subscription**. Keep
it somewhere that doesn't depend on that subscription (for example an offline or separately administered store), together
with a note of the subscription and region. It isn't usable on its own, but treat it as sensitive.

Also check, every quarter:
- the vault still has purge protection: `az keyvault show -n $kv --query properties.enablePurgeProtection`;
- the key's role assignments haven't changed (only the app's identity has *Key Vault Crypto Service Encryption User*).

## 2. Recovering from common incidents

**The vault or the key was deleted.** Recover it within 90 days:

```powershell
az keyvault recover --name <vault name>                      # the whole vault
az keyvault key recover --vault-name <vault name> --name mbr-master
```

Then run `terraform apply`, which leaves the recovered vault and key in place (`recover_soft_deleted_key_vaults`).

**The key is gone for good** (past 90 days, or a different subscription). Create a vault in the same geography and subscription
as the backup, restore the key into it, and point the app at it:

```powershell
az keyvault key restore --vault-name <new vault> --file mbr-master.keybackup
```

Set `Mbr__Encryption__KeyVaultKeyId` to the restored key's **versioned** ID (the same version as before), and give the app's
identity *Key Vault Crypto Service Encryption User* on it.

**Bad data in PostgreSQL** (for example a mistaken bulk delete). Restore the server to a point in time into a new server:

```powershell
az postgres flexible-server restore -g <resource group> -n <new server> --source-server <server> --restore-time "2026-11-20T09:00:00Z"
```

Then point `Mbr__Database__ConnectionString` at the new server (and make the app's identity its Entra administrator), or swap
the servers. Snapshots taken after the restore point are still in S3 but not in the index; run new backups to continue.

**Lost or damaged objects in S3.** With versioning, restore the previous versions of the affected keys. With Object Lock, locked
objects can't be deleted or overwritten before their retention ends.

## 3. Rebuilding a deployment

If the subscription or the resource group is lost, but you still have the S3 bucket and the master key (in the vault or as a
backup):

1. Restore the master key into a vault in the same geography and subscription (section 2), or keep the recovered vault.
2. Run `terraform apply` into a new resource group. If the old vault is gone, import the new one so Terraform manages it, or
   set `Mbr__Encryption__KeyVaultKeyId` to the restored key.
3. Restore PostgreSQL from its point-in-time backup if it still exists. That's the supported path: keep the retention long
   enough to cover how long it would take you to notice a problem.

   If the database is gone for good, the app starts with an empty index and you add the tenants again; new backups work
   straight away. The old backups are still in S3 and can still be decrypted (master key + `tenants/<tenant id>/key.json`), but
   MBR has no tool yet to re-import them into a new database, so treat point-in-time restore as essential.
4. If the Entra app registrations were recreated, customers grant admin consent again (Tenants → consent links).

## 4. Test it

Twice a year, in a separate subscription or resource group:
1. restore the master key backup into a new vault;
2. restore PostgreSQL to a point in time;
3. run the app against both, open a snapshot and restore one item into a test tenant.

Write down how long each step took; that's your real recovery time.

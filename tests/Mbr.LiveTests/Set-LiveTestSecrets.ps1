<#
.SYNOPSIS
  Stores what the "Live smoke tests" workflow needs as secrets of the repository's live-tests environment.

.DESCRIPTION
  Run it yourself, signed in with `gh auth login` as someone who can administer the repository. Use a TEST tenant only: the
  tests create, delete and restore a group there. The certificates are read from disk and sent to GitHub encrypted; nothing is
  written anywhere else.

.EXAMPLE
  ./tests/Mbr.LiveTests/Set-LiveTestSecrets.ps1 -Tenant contoso-test.onmicrosoft.com `
      -BackupClientId <backup app id> -BackupPfx C:\certs\backup.pfx `
      -RestoreClientId <restore app id> -RestorePfx C:\certs\restore.pfx
#>
param(
    [Parameter(Mandatory)] [string] $Tenant,
    [Parameter(Mandatory)] [string] $BackupClientId,
    [Parameter(Mandatory)] [string] $BackupPfx,
    [Parameter(Mandatory)] [string] $RestoreClientId,
    [Parameter(Mandatory)] [string] $RestorePfx,
    [string] $Repository = (gh repo view --json nameWithOwner --jq .nameWithOwner)
)
$ErrorActionPreference = 'Stop'

foreach ($f in $BackupPfx, $RestorePfx) { if (-not (Test-Path $f)) { throw "Certificate not found: $f" } }
$password = Read-Host -AsSecureString 'PFX password (Enter if none)'
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))

# The environment lets you add required reviewers before a run (Settings > Environments > live-tests).
gh api -X PUT "repos/$Repository/environments/live-tests" --silent

function Set-Secret([string] $Name, [string] $Value) {
    $Value | gh secret set $Name --repo $Repository --env live-tests
}
Set-Secret MBR_LIVE_TENANT $Tenant
Set-Secret MBR_LIVE_BACKUP_CLIENT_ID $BackupClientId
Set-Secret MBR_LIVE_RESTORE_CLIENT_ID $RestoreClientId
Set-Secret MBR_LIVE_BACKUP_PFX_BASE64 ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $BackupPfx))))
Set-Secret MBR_LIVE_RESTORE_PFX_BASE64 ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $RestorePfx))))
if ($plain) { Set-Secret MBR_LIVE_PFX_PASSWORD $plain } # unset means no password
$plain = $null

Write-Host "Done. Run it from Actions > Live smoke tests > Run workflow, or: gh workflow run live.yml --repo $Repository"

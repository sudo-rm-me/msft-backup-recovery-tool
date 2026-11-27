# Security policy

MBR holds tenant-wide permissions in every customer tenant it backs up, so security reports are taken seriously.

## Reporting a vulnerability

Please **don't open a public issue**. Report it privately instead:

- On GitHub: the repository's **Security** tab → **Report a vulnerability**.

Include what you found, how to reproduce it, and what an attacker could do with it. You'll get an acknowledgement within a
few working days, and a fix or a plan once the report is confirmed. Credit is given in the release notes unless you'd rather
stay anonymous.

## Supported versions

Fixes go into the latest release. Update by deploying the newest release's image (docs/setup.md, "Where the image comes
from").

## Scope

In scope: the API and web console, the CLI, the job worker, the Terraform module, and the container image.

Out of scope: weaknesses in Microsoft Graph, Azure or AWS themselves, and findings that need an operator who is already a
global admin of the deployment.

## Hardening already in place

- Backups are encrypted per tenant (AES-256-GCM) with keys wrapped by a Key Vault key that never leaves the vault.
- The backup app only reads; write permissions sit on a separate restore app that customers consent to separately.
- No passwords or long-lived keys: managed identity for Azure, Entra sign-in for PostgreSQL, federation for S3.
- Every operator action is written to the audit log.
- Optional: an IP allow-list for the console and private networking for PostgreSQL and Key Vault (docs/setup.md).

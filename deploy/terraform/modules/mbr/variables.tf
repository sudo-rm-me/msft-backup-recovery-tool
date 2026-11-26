variable "location" {
  description = "Azure region, e.g. westeurope."
  type        = string
}

variable "resource_group_name" {
  description = "Existing resource group to deploy into. Empty: the module creates rg-<name> in location."
  type        = string
  default     = ""
}

variable "source_dir" {
  description = "Path to a checkout of the MBR repository (the folder with the Dockerfile), used to build the image. Empty: the repository this module sits in."
  type        = string
  default     = ""
}

variable "name" {
  description = "Short lowercase name used in resource names (letters and digits, 3-12 characters)."
  type        = string
  default     = "mbr"

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.name))
    error_message = "Use 3-12 lowercase letters and digits, starting with a letter."
  }
}

variable "image_tag" {
  description = "Build the image from the MBR source with this tag, e.g. the git commit: terraform apply -var image_tag=$(git rev-parse --short HEAD). A new tag rebuilds the image in the registry and rolls out a new revision. Leave empty when image is set."
  type        = string
  default     = ""

  validation {
    condition     = var.image_tag == "" || can(regex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", var.image_tag))
    error_message = "Use a valid image tag (letters, digits, '_', '.', '-')."
  }
}

variable "image" {
  description = "Prebuilt MBR image to run instead of building one, e.g. contosoacr.azurecr.io/mbr:1.4.0. When set, nothing is built: image_tag and source_dir are ignored, and neither the Azure CLI nor the MBR source is needed. A new value rolls out a new revision."
  type        = string
  default     = ""
}

variable "image_registry_id" {
  description = "Resource ID of the Azure Container Registry that holds image, when it's not this deployment's own registry. The app's identity gets AcrPull on it. Leave empty for the deployment's own registry or a public image."
  type        = string
  default     = ""
}

variable "app_display_name_prefix" {
  description = "Prefix of the Entra app registrations' display names (\"MBR Backup\", \"MBR Restore\", \"MBR Console\", \"MBR AWS federation\")."
  type        = string
  default     = "MBR"
}

variable "bootstrap_admins" {
  description = "User principal names that become MBR global admins on first sign-in."
  type        = list(string)
  default     = []
}

variable "custom_domain" {
  description = "Optional host name (e.g. mbr.contoso.com) to serve MBR on instead of the default *.azurecontainerapps.io name. Bind it to the container app as described in docs/setup.md."
  type        = string
  default     = ""
}

# ---- S3 (existing bucket; the app reaches it with AWS federation, no keys)

variable "s3_bucket" {
  description = "Existing S3 bucket for backups."
  type        = string
}

variable "s3_region" {
  description = "The bucket's AWS region, e.g. eu-west-1."
  type        = string
}

variable "s3_prefix" {
  description = "Optional key prefix (folder) inside the bucket."
  type        = string
  default     = ""
}

variable "aws_role_arn" {
  description = "IAM role the app assumes for S3. Leave empty on the first apply, create the role from the outputs (docs/setup.md), then set it and apply again."
  type        = string
  default     = ""
}

variable "aws_account_id" {
  description = "Optional AWS account ID, only used to fill in the trust policy shown in the outputs."
  type        = string
  default     = ""
}

variable "s3_object_lock_days" {
  description = "Object Lock retention written on every object (0 = off). The bucket must have Object Lock enabled."
  type        = number
  default     = 0
}

variable "s3_object_lock_mode" {
  description = "GOVERNANCE or COMPLIANCE."
  type        = string
  default     = "GOVERNANCE"

  validation {
    condition     = contains(["GOVERNANCE", "COMPLIANCE"], var.s3_object_lock_mode)
    error_message = "Use GOVERNANCE or COMPLIANCE."
  }
}

variable "s3_storage_class" {
  description = "Optional S3 storage class, e.g. STANDARD_IA or INTELLIGENT_TIERING."
  type        = string
  default     = ""
}

# ---- Sizing

variable "cpu" {
  description = "vCPUs per replica (Consumption plan: 0.25-4, with memory at 2 GiB per vCPU)."
  type        = number
  default     = 2
}

variable "memory" {
  description = "Memory per replica, e.g. 4Gi (must match cpu: 2 GiB per vCPU)."
  type        = string
  default     = "4Gi"
}

variable "min_replicas" {
  description = "At least 1: the scheduler and job worker run inside the app."
  type        = number
  default     = 1

  validation {
    condition     = var.min_replicas >= 1
    error_message = "At least one replica must always run (the scheduler and jobs live in the app)."
  }
}

variable "max_replicas" {
  description = "Replicas share jobs through leases in the database; add more to back up more tenants in parallel."
  type        = number
  default     = 1
}

variable "postgres_sku" {
  description = "Azure Database for PostgreSQL Flexible Server SKU."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "postgres_storage_mb" {
  type    = number
  default = 32768
}

variable "postgres_backup_retention_days" {
  description = "Point-in-time restore window for the metadata database (7-35 days)."
  type        = number
  default     = 14
}

variable "certificate_generation" {
  description = "Bump to renew both app certificates: new ones are created in Key Vault (deleted names can't be reused there) and registered on the apps."
  type        = number
  default     = 1
}

variable "certificate_validity_months" {
  description = "Validity of the backup and restore apps' certificates (created in Key Vault; private keys never leave it)."
  type        = number
  default     = 24
}

# ---- Optional settings

variable "notification_webhooks" {
  description = "URLs that receive job-failure and drift alerts (JSON with a text field: Teams or Slack workflows). Stored as container app secrets."
  type        = list(string)
  default     = []
  sensitive   = true
}

variable "partner_tenant_id" {
  description = "Your own tenant ID, to import customers from GDAP relationships (docs/setup.md)."
  type        = string
  default     = ""
}

variable "tags" {
  type    = map(string)
  default = {}
}

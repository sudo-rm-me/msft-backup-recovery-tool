# Azure resources for MBR: everything the stateless container needs lives outside it.
#   PostgreSQL (metadata, Entra auth only) · Key Vault (master key + app certificates) · Container Registry · Log Analytics
#   · a user-assigned managed identity the app uses for all of them (and, through federation, for the S3 bucket).

data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
}

locals {
  suffix      = random_string.suffix.result
  base        = "${var.name}-${local.suffix}"
  tenant_id   = data.azurerm_client_config.current.tenant_id
  public_host = var.custom_domain != "" ? var.custom_domain : "${var.name}.${azurerm_container_app_environment.main.default_domain}"
  public_url  = "https://${local.public_host}"
  tags        = merge({ app = "mbr" }, var.tags)
}

resource "azurerm_resource_group" "main" {
  count    = var.resource_group_name == "" ? 1 : 0
  name     = "rg-${var.name}"
  location = var.location
  tags     = local.tags
}

data "azurerm_resource_group" "existing" {
  count = var.resource_group_name == "" ? 0 : 1
  name  = var.resource_group_name
}

locals {
  resource_group_name = var.resource_group_name == "" ? azurerm_resource_group.main[0].name : data.azurerm_resource_group.existing[0].name
  # Resources go in var.location, even when an existing resource group lives elsewhere.
  location   = var.location
  source_dir = var.source_dir != "" ? var.source_dir : abspath("${path.module}/../../../..")
}

resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-${local.base}"
  resource_group_name = local.resource_group_name
  location            = local.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "app" {
  name                = "id-${local.base}"
  resource_group_name = local.resource_group_name
  location            = local.location
  tags                = local.tags
}

# ---------------------------------------------------------------------------------------------------- container registry

resource "azurerm_container_registry" "main" {
  name                = "cr${var.name}${local.suffix}"
  resource_group_name = local.resource_group_name
  location            = local.location
  sku                 = "Basic"
  admin_enabled       = false
  tags                = local.tags
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

# ---------------------------------------------------------------------------------------------------- key vault

resource "azurerm_key_vault" "main" {
  name                       = "kv-${local.base}"
  resource_group_name        = local.resource_group_name
  location                   = local.location
  tenant_id                  = local.tenant_id
  sku_name                   = "standard"
  enable_rbac_authorization  = true
  purge_protection_enabled   = true # losing the master key would make every backup unreadable
  soft_delete_retention_days = 90
  tags                       = local.tags

  lifecycle {
    prevent_destroy = true
  }
}

# Whoever runs Terraform creates the key and certificates (data plane), so needs a data-plane role.
resource "azurerm_role_assignment" "kv_deployer" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Administrator"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "time_sleep" "kv_rbac" {
  depends_on      = [azurerm_role_assignment.kv_deployer]
  create_duration = "60s"
}

# Wraps each tenant's data-encryption key. The app pins this version: wrapped keys need the same version to unwrap.
resource "azurerm_key_vault_key" "master" {
  name         = "mbr-master"
  key_vault_id = azurerm_key_vault.main.id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey"]
  depends_on   = [time_sleep.kv_rbac]

  lifecycle {
    prevent_destroy = true
  }
}

# Certificates the backup and restore apps authenticate with. Generated in Key Vault; the app downloads them at startup.
resource "azurerm_key_vault_certificate" "app" {
  for_each     = toset(["backup", "restore"])
  name         = "mbr-${each.key}-${var.certificate_generation}"
  key_vault_id = azurerm_key_vault.main.id
  depends_on   = [time_sleep.kv_rbac]

  lifecycle {
    create_before_destroy = true # the app keeps working with the old certificate until the new revision starts
  }

  certificate_policy {
    issuer_parameters {
      name = "Self"
    }
    key_properties {
      exportable = true # the app and its PowerShell modules load the PFX
      key_type   = "RSA"
      key_size   = 3072
      reuse_key  = false
    }
    secret_properties {
      content_type = "application/x-pkcs12"
    }
    x509_certificate_properties {
      subject            = "CN=${var.app_display_name_prefix} ${title(each.key)}"
      validity_in_months = var.certificate_validity_months
      key_usage          = ["digitalSignature"]
    }
    # Renewal must also update the app registration, so it's done with Terraform (certificate_generation), not automatically.
    lifetime_action {
      action {
        action_type = "EmailContacts"
      }
      trigger {
        days_before_expiry = 30
      }
    }
  }
}

resource "azurerm_role_assignment" "kv_master_key" {
  scope                = azurerm_key_vault_key.master.resource_versionless_id
  role_definition_name = "Key Vault Crypto Service Encryption User" # get, wrapKey, unwrapKey
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "kv_certificates" {
  for_each             = azurerm_key_vault_certificate.app
  scope                = "${azurerm_key_vault.main.id}/secrets/${each.value.name}"
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

# ---------------------------------------------------------------------------------------------------- postgresql

resource "azurerm_postgresql_flexible_server" "main" {
  name                          = "psql-${local.base}"
  resource_group_name           = local.resource_group_name
  location                      = local.location
  version                       = "16"
  sku_name                      = var.postgres_sku
  storage_mb                    = var.postgres_storage_mb
  backup_retention_days         = var.postgres_backup_retention_days
  public_network_access_enabled = true
  tags                          = local.tags

  # No passwords: only Microsoft Entra identities can sign in.
  authentication {
    active_directory_auth_enabled = true
    password_auth_enabled         = false
    tenant_id                     = local.tenant_id
  }

  lifecycle {
    prevent_destroy = true
    ignore_changes  = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "mbr" {
  name      = "mbr"
  server_id = azurerm_postgresql_flexible_server.main.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# The app's identity is the server's Entra administrator (it creates and migrates its own schema).
resource "azurerm_postgresql_flexible_server_active_directory_administrator" "app" {
  server_name         = azurerm_postgresql_flexible_server.main.name
  resource_group_name = local.resource_group_name
  tenant_id           = local.tenant_id
  object_id           = azurerm_user_assigned_identity.app.principal_id
  principal_name      = azurerm_user_assigned_identity.app.name
  principal_type      = "ServicePrincipal"
}

# Container Apps (Consumption) has no fixed outbound IPs, so allow Azure services; sign-in still requires Entra.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# ---------------------------------------------------------------------------------------------------- container apps environment

resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${local.base}"
  resource_group_name        = local.resource_group_name
  location                   = local.location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
  tags                       = local.tags
}

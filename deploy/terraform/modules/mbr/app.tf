# The image and the container app that runs it. The image is either built here from the MBR source (image_tag) or a
# prebuilt one (image).

locals {
  build_image = var.image == ""
  image       = local.build_image ? "${azurerm_container_registry.main.login_server}/mbr:${var.image_tag}" : var.image
  # Registry host of a prebuilt image in another Azure Container Registry, pulled with the app's identity.
  image_registry = var.image != "" && var.image_registry_id != "" ? lower(split("/", var.image)[0]) : ""
}

# Builds the repo's Dockerfile in Azure Container Registry whenever image_tag changes. Needs the Azure CLI, signed in, and
# the MBR source (source_dir). Skipped when a prebuilt image is given.
resource "terraform_data" "image" {
  count            = local.build_image ? 1 : 0
  triggers_replace = [var.image_tag, azurerm_container_registry.main.id]

  provisioner "local-exec" {
    working_dir = local.source_dir
    command     = "az acr build --subscription ${data.azurerm_client_config.current.subscription_id} --registry ${azurerm_container_registry.main.name} --image mbr:${var.image_tag} --file Dockerfile ."
  }
}

# Lets the app pull a prebuilt image from your own registry.
resource "azurerm_role_assignment" "image_registry_pull" {
  count                = local.image_registry != "" ? 1 : 0
  scope                = var.image_registry_id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
  principal_type       = "ServicePrincipal"
}

# Role assignments take a while to reach Key Vault, PostgreSQL and the registry; give them time before the first start.
resource "time_sleep" "app_rbac" {
  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.image_registry_pull,
    azurerm_role_assignment.kv_master_key,
    azurerm_role_assignment.kv_certificates,
    azurerm_postgresql_flexible_server_active_directory_administrator.app,
    azuread_app_role_assignment.aws,
  ]
  create_duration = "90s"
}

locals {
  webhooks = nonsensitive(length(var.notification_webhooks)) # only the count is used for naming

  env = merge(
    {
      ASPNETCORE_ENVIRONMENT = "Production"
      AZURE_CLIENT_ID        = azurerm_user_assigned_identity.app.client_id # DefaultAzureCredential uses this managed identity
      Mbr__PublicBaseUrl     = local.public_url

      Mbr__Database__Provider         = "Postgres"
      Mbr__Database__EntraAuth        = "true"
      Mbr__Database__ConnectionString = "Host=${azurerm_postgresql_flexible_server.main.fqdn};Database=${azurerm_postgresql_flexible_server_database.mbr.name};Username=${azurerm_user_assigned_identity.app.name};SSL Mode=Require"

      Mbr__Encryption__KeyWrapper    = "KeyVault"
      Mbr__Encryption__KeyVaultKeyId = azurerm_key_vault_key.master.id # versioned on purpose

      Mbr__Apps__Backup__ClientId             = azuread_application.daemon["backup"].client_id
      Mbr__Apps__Backup__KeyVaultCertificate  = azurerm_key_vault_certificate.app["backup"].versionless_secret_id
      Mbr__Apps__Restore__ClientId            = azuread_application.daemon["restore"].client_id
      Mbr__Apps__Restore__KeyVaultCertificate = azurerm_key_vault_certificate.app["restore"].versionless_secret_id

      Mbr__Storage__DefaultS3__Bucket              = var.s3_bucket
      Mbr__Storage__DefaultS3__Region              = var.s3_region
      Mbr__Storage__DefaultS3__Credentials         = "AzureFederation"
      Mbr__Storage__DefaultS3__RoleArn             = var.aws_role_arn
      Mbr__Storage__DefaultS3__WebIdentityAudience = azuread_application_identifier_uri.aws.identifier_uri

      Auth__Mode               = "Entra"
      Auth__Entra__Instance    = "https://login.microsoftonline.com/"
      Auth__Entra__TenantId    = local.tenant_id
      Auth__Entra__ClientId    = azuread_application.console.client_id
      Auth__Entra__Audience    = azuread_application_identifier_uri.console.identifier_uri
      Auth__Entra__SpaClientId = azuread_application.console.client_id
      Auth__Entra__Scope       = "${azuread_application_identifier_uri.console.identifier_uri}/access_as_user"
    },
    var.s3_prefix != "" ? { Mbr__Storage__DefaultS3__Prefix = var.s3_prefix } : {},
    var.s3_storage_class != "" ? { Mbr__Storage__DefaultS3__StorageClass = var.s3_storage_class } : {},
    var.s3_object_lock_days > 0 ? {
      Mbr__Storage__DefaultS3__ObjectLockDays = tostring(var.s3_object_lock_days)
      Mbr__Storage__DefaultS3__ObjectLockMode = var.s3_object_lock_mode
    } : {},
    var.partner_tenant_id != "" ? { Mbr__Partner__TenantId = var.partner_tenant_id } : {},
    { for i, upn in var.bootstrap_admins : "Auth__BootstrapAdmins__${i}" => upn },
  )
}

resource "azurerm_container_app" "main" {
  name                         = var.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = local.resource_group_name
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.main.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  dynamic "registry" {
    for_each = local.image_registry != "" && local.image_registry != lower(azurerm_container_registry.main.login_server) ? [local.image_registry] : []
    content {
      server   = registry.value
      identity = azurerm_user_assigned_identity.app.id
    }
  }

  dynamic "secret" {
    for_each = range(local.webhooks)
    content {
      name  = "webhook-${secret.value}"
      value = var.notification_webhooks[secret.value]
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "mbr"
      image  = local.image
      cpu    = var.cpu
      memory = var.memory

      dynamic "env" {
        for_each = local.env
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = range(local.webhooks)
        content {
          name        = "Mbr__Notifications__Webhooks__${env.value}"
          secret_name = "webhook-${env.value}"
        }
      }

      # Startup includes database migrations and downloading the certificates.
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/healthz"
        initial_delay           = 10
        interval_seconds        = 15
        failure_count_threshold = 10
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/healthz"
        interval_seconds        = 30
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/healthz"
        interval_seconds        = 10
        failure_count_threshold = 3
      }
    }
  }

  lifecycle {
    precondition {
      condition     = var.image != "" || var.image_tag != ""
      error_message = "Set image_tag (build the image from the MBR source) or image (run a prebuilt image)."
    }
  }

  depends_on = [terraform_data.image, time_sleep.app_rbac]
}

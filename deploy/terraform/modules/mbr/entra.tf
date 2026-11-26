# Microsoft Entra app registrations in your home (operator) tenant.
#   Backup / Restore: multi-tenant apps customer tenants consent to (certificates from Key Vault).
#   Console: operator sign-in for the web UI and CLI.
#   AWS federation: the audience of the token the app's managed identity trades for temporary AWS credentials.

data "azuread_client_config" "current" {}

data "azuread_application_published_app_ids" "well_known" {}

data "azuread_service_principal" "graph" {
  client_id = data.azuread_application_published_app_ids.well_known.result["MicrosoftGraph"]
}

data "azuread_service_principal" "exchange" {
  client_id = data.azuread_application_published_app_ids.well_known.result["Office365ExchangeOnline"]
}

locals {
  # Kept identical to src/Mbr.Core/Permissions/PermissionCatalog.cs by a unit test.
  permissions      = jsondecode(file("${path.module}/permissions.json"))
  consent_redirect = "${local.public_url}/api/onboarding/consent-callback"
  owners           = [data.azuread_client_config.current.object_id]
}

# ---------------------------------------------------------------------------------------------------- backup and restore apps

resource "azuread_application" "daemon" {
  for_each         = toset(["backup", "restore"])
  display_name     = "${var.app_display_name_prefix} ${title(each.key)}"
  sign_in_audience = "AzureADMultipleOrgs"
  owners           = local.owners

  web {
    redirect_uris = [local.consent_redirect]
  }

  required_resource_access {
    resource_app_id = data.azuread_service_principal.graph.client_id
    dynamic "resource_access" {
      for_each = local.permissions[each.key].graph
      content {
        id   = data.azuread_service_principal.graph.app_role_ids[resource_access.value]
        type = "Role"
      }
    }
  }

  required_resource_access {
    resource_app_id = data.azuread_service_principal.exchange.client_id
    dynamic "resource_access" {
      for_each = local.permissions[each.key].exchange
      content {
        id   = data.azuread_service_principal.exchange.app_role_ids[resource_access.value]
        type = "Role"
      }
    }
  }
}

resource "azuread_service_principal" "daemon" {
  for_each  = azuread_application.daemon
  client_id = each.value.client_id
  owners    = local.owners
}

resource "azuread_application_certificate" "daemon" {
  for_each       = azuread_application.daemon
  application_id = each.value.id
  type           = "AsymmetricX509Cert"
  encoding       = "hex"
  value          = azurerm_key_vault_certificate.app[each.key].certificate_data
  end_date       = azurerm_key_vault_certificate.app[each.key].certificate_attribute[0].expires
}

# ---------------------------------------------------------------------------------------------------- console (operator sign-in)

resource "random_uuid" "console_scope" {}

resource "azuread_application" "console" {
  display_name                   = "${var.app_display_name_prefix} Console"
  sign_in_audience               = "AzureADMyOrg"
  owners                         = local.owners
  fallback_public_client_enabled = true # the CLI signs in with device code / localhost redirect

  single_page_application {
    redirect_uris = [local.public_url, "http://localhost:5173", "http://localhost:5180"] # the console signs in with redirectUri = window.location.origin
  }

  public_client {
    redirect_uris = ["http://localhost"]
  }

  api {
    requested_access_token_version = 2
    oauth2_permission_scope {
      id                         = random_uuid.console_scope.result
      value                      = "access_as_user"
      type                       = "User"
      enabled                    = true
      admin_consent_display_name = "Use MBR"
      admin_consent_description  = "Sign in to the MBR console and API"
      user_consent_display_name  = "Use MBR"
      user_consent_description   = "Sign in to the MBR console and API"
    }
  }

  lifecycle {
    ignore_changes = [identifier_uris] # managed by azuread_application_identifier_uri.console
  }
}

resource "azuread_application_identifier_uri" "console" {
  application_id = azuread_application.console.id
  identifier_uri = "api://${azuread_application.console.client_id}"
}

resource "azuread_service_principal" "console" {
  client_id = azuread_application.console.client_id
  owners    = local.owners
}

# Tenant-wide consent for the console to call its own API, so operators aren't prompted.
resource "azuread_service_principal_delegated_permission_grant" "console" {
  service_principal_object_id          = azuread_service_principal.console.object_id
  resource_service_principal_object_id = azuread_service_principal.console.object_id
  claim_values                         = ["access_as_user"]
}

# ---------------------------------------------------------------------------------------------------- AWS federation (S3 without keys)

resource "random_uuid" "aws_role" {}

resource "azuread_application" "aws" {
  display_name     = "${var.app_display_name_prefix} AWS federation"
  sign_in_audience = "AzureADMyOrg"
  owners           = local.owners

  api {
    requested_access_token_version = 2 # issuer https://login.microsoftonline.com/<tenant>/v2.0, audience = this app's client ID
  }

  app_role {
    id                   = random_uuid.aws_role.result
    value                = "S3.Access"
    display_name         = "Access the MBR S3 bucket"
    description          = "Lets the MBR app trade its token for AWS credentials."
    allowed_member_types = ["Application"]
    enabled              = true
  }

  lifecycle {
    ignore_changes = [identifier_uris] # managed by azuread_application_identifier_uri.aws
  }
}

resource "azuread_application_identifier_uri" "aws" {
  application_id = azuread_application.aws.id
  identifier_uri = "api://${azuread_application.aws.client_id}"
}

# Only identities assigned the role can get a token for this audience: just the app's managed identity.
resource "azuread_service_principal" "aws" {
  client_id                    = azuread_application.aws.client_id
  app_role_assignment_required = true
  owners                       = local.owners
}

resource "azuread_app_role_assignment" "aws" {
  app_role_id         = random_uuid.aws_role.result
  principal_object_id = azurerm_user_assigned_identity.app.principal_id
  resource_object_id  = azuread_service_principal.aws.object_id
}

# ---------------------------------------------------------------------------------------------------- GDAP (optional)

# Importing customers from GDAP relationships reads your own tenant: grant (admin-consent) these to the backup app here only.
# They aren't in its requested permissions, so customer tenants are never asked for them.
resource "azuread_app_role_assignment" "gdap" {
  for_each            = var.partner_tenant_id != "" ? toset(["DelegatedAdminRelationship.Read.All", "CrossTenantInformation.ReadBasic.All"]) : toset([])
  app_role_id         = data.azuread_service_principal.graph.app_role_ids[each.value]
  principal_object_id = azuread_service_principal.daemon["backup"].object_id
  resource_object_id  = data.azuread_service_principal.graph.object_id
}

output "url" {
  description = "Where operators sign in."
  value       = local.public_url
}

output "image" {
  description = "The image the container app runs."
  value       = local.image
}

output "container_registry" {
  description = "This deployment's Azure Container Registry (login server), e.g. to push prebuilt images to."
  value       = azurerm_container_registry.main.login_server
}

output "default_host_name" {
  description = "The container app's own host name (the CNAME target for a custom domain)."
  value       = azurerm_container_app.main.ingress[0].fqdn
}

output "consent_redirect_uri" {
  value = local.consent_redirect
}

output "backup_app_client_id" {
  value = azuread_application.daemon["backup"].client_id
}

output "restore_app_client_id" {
  value = azuread_application.daemon["restore"].client_id
}

output "console_app_client_id" {
  value = azuread_application.console.client_id
}

output "key_vault_name" {
  value = azurerm_key_vault.main.name
}

output "managed_identity_principal_id" {
  value = azurerm_user_assigned_identity.app.principal_id
}

# ---- What to set up in AWS (docs/setup.md, "S3 access")

locals {
  aws_oidc_host     = "login.microsoftonline.com/${local.tenant_id}/v2.0"
  aws_account       = var.aws_account_id != "" ? var.aws_account_id : "<AWS_ACCOUNT_ID>"
  aws_bucket_prefix = var.s3_prefix != "" ? "${trim(var.s3_prefix, "/")}/*" : "*"
}

output "aws_oidc_provider_url" {
  description = "IAM identity provider (OpenID Connect) URL."
  value       = "https://${local.aws_oidc_host}"
}

output "aws_oidc_audience" {
  description = "IAM identity provider audience (client ID)."
  value       = azuread_application.aws.client_id
}

output "aws_federation_audience_uri" {
  value = azuread_application_identifier_uri.aws.identifier_uri
}

output "aws_role_trust_policy" {
  description = "Trust policy for the IAM role: only this deployment's managed identity can assume it."
  value       = jsonencode({
    Version   = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Federated = "arn:aws:iam::${local.aws_account}:oidc-provider/${local.aws_oidc_host}" }
      Action    = "sts:AssumeRoleWithWebIdentity"
      Condition = {
        StringEquals = {
          "${local.aws_oidc_host}:aud" = azuread_application.aws.client_id
          "${local.aws_oidc_host}:sub" = azurerm_user_assigned_identity.app.principal_id
        }
      }
    }]
  })
}

output "aws_role_permissions_policy" {
  description = "Permissions for the IAM role on the backup bucket."
  value       = jsonencode({
    Version   = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["s3:ListBucket"]
        Resource = "arn:aws:s3:::${var.s3_bucket}"
      },
      {
        Effect   = "Allow"
        Action   = ["s3:PutObject", "s3:GetObject", "s3:DeleteObject", "s3:PutObjectRetention"]
        Resource = "arn:aws:s3:::${var.s3_bucket}/${local.aws_bucket_prefix}"
      },
    ]
  })
}

output "next_steps" {
  value = var.aws_role_arn == "" ? "Create the IAM identity provider and role from the aws_* outputs (docs/setup.md, \"S3 access\"), then apply again with -var aws_role_arn=<role ARN>." : "Open ${local.public_url} and sign in. Then add customer tenants (Tenants > Add tenant) and send their admins the consent links."
}

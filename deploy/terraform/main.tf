# Standalone deployment: the MBR module with this folder's variables. To deploy from your own Terraform repository
# instead, call modules/mbr from there (docs/setup.md, "Deploy from an existing Terraform repository").

module "mbr" {
  source = "./modules/mbr"

  location                       = var.location
  resource_group_name            = var.resource_group_name
  source_dir                     = var.source_dir
  name                           = var.name
  image_tag                      = var.image_tag
  image                          = var.image
  image_registry_id              = var.image_registry_id
  app_display_name_prefix        = var.app_display_name_prefix
  bootstrap_admins               = var.bootstrap_admins
  custom_domain                  = var.custom_domain
  s3_bucket                      = var.s3_bucket
  s3_region                      = var.s3_region
  s3_prefix                      = var.s3_prefix
  aws_role_arn                   = var.aws_role_arn
  aws_account_id                 = var.aws_account_id
  s3_object_lock_days            = var.s3_object_lock_days
  s3_object_lock_mode            = var.s3_object_lock_mode
  s3_storage_class               = var.s3_storage_class
  allowed_ip_ranges              = var.allowed_ip_ranges
  private_networking             = var.private_networking
  vnet_address_space             = var.vnet_address_space
  management_ip_ranges           = var.management_ip_ranges
  cpu                            = var.cpu
  memory                         = var.memory
  min_replicas                   = var.min_replicas
  max_replicas                   = var.max_replicas
  postgres_sku                   = var.postgres_sku
  postgres_storage_mb            = var.postgres_storage_mb
  postgres_backup_retention_days = var.postgres_backup_retention_days
  certificate_generation         = var.certificate_generation
  certificate_validity_months    = var.certificate_validity_months
  notification_webhooks          = var.notification_webhooks
  partner_tenant_id              = var.partner_tenant_id
  tags                           = var.tags
}

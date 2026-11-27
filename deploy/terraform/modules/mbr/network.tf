# Optional private networking (private_networking = true): a virtual network for the Container Apps environment,
# PostgreSQL reachable only inside it, and Key Vault through a private endpoint. Only for new deployments: PostgreSQL's
# network mode can't change after it's created.
#
#   vnet_address_space (a /22)
#     ├─ first /23      Container Apps environment
#     ├─ x.x.2.0/28     PostgreSQL (delegated)
#     └─ x.x.3.0/27     private endpoints (Key Vault)

locals {
  private = var.private_networking
}

resource "azurerm_virtual_network" "main" {
  count               = local.private ? 1 : 0
  name                = "vnet-${local.base}"
  resource_group_name = local.resource_group_name
  location            = local.location
  address_space       = [var.vnet_address_space]
  tags                = local.tags
}

resource "azurerm_subnet" "apps" {
  count                = local.private ? 1 : 0
  name                 = "snet-apps"
  resource_group_name  = local.resource_group_name
  virtual_network_name = azurerm_virtual_network.main[0].name
  address_prefixes     = [cidrsubnet(var.vnet_address_space, 1, 0)]
}

resource "azurerm_subnet" "postgres" {
  count                = local.private ? 1 : 0
  name                 = "snet-postgres"
  resource_group_name  = local.resource_group_name
  virtual_network_name = azurerm_virtual_network.main[0].name
  address_prefixes     = [cidrsubnet(var.vnet_address_space, 6, 32)]

  delegation {
    name = "postgres"
    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet" "endpoints" {
  count                = local.private ? 1 : 0
  name                 = "snet-endpoints"
  resource_group_name  = local.resource_group_name
  virtual_network_name = azurerm_virtual_network.main[0].name
  address_prefixes     = [cidrsubnet(var.vnet_address_space, 5, 24)]
}

# ---- PostgreSQL: VNet-integrated, no public endpoint

resource "azurerm_private_dns_zone" "postgres" {
  count               = local.private ? 1 : 0
  name                = "${var.name}${local.suffix}.private.postgres.database.azure.com"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  count                 = local.private ? 1 : 0
  name                  = "postgres"
  resource_group_name   = local.resource_group_name
  private_dns_zone_name = azurerm_private_dns_zone.postgres[0].name
  virtual_network_id    = azurerm_virtual_network.main[0].id
  tags                  = local.tags
}

# ---- Key Vault: private endpoint for the app; the public endpoint only admits management_ip_ranges (where Terraform runs)

resource "azurerm_private_dns_zone" "key_vault" {
  count               = local.private ? 1 : 0
  name                = "privatelink.vaultcore.azure.net"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "key_vault" {
  count                 = local.private ? 1 : 0
  name                  = "key-vault"
  resource_group_name   = local.resource_group_name
  private_dns_zone_name = azurerm_private_dns_zone.key_vault[0].name
  virtual_network_id    = azurerm_virtual_network.main[0].id
  tags                  = local.tags
}

resource "azurerm_private_endpoint" "key_vault" {
  count               = local.private ? 1 : 0
  name                = "pe-kv-${local.base}"
  resource_group_name = local.resource_group_name
  location            = local.location
  subnet_id           = azurerm_subnet.endpoints[0].id
  tags                = local.tags

  private_service_connection {
    name                           = "key-vault"
    private_connection_resource_id = azurerm_key_vault.main.id
    subresource_names              = ["vault"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "key-vault"
    private_dns_zone_ids = [azurerm_private_dns_zone.key_vault[0].id]
  }
}

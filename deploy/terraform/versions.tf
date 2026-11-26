terraform {
  required_version = ">= 1.6"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.20"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.12"
    }
  }

  # Keep state in Azure Storage for anything beyond a trial: copy backend.tf.example to backend.tf.
}

provider "azurerm" {
  subscription_id = var.subscription_id

  features {
    key_vault {
      # The vault holds the master key: never purge it as part of a destroy.
      purge_soft_delete_on_destroy       = false
      purge_soft_deleted_keys_on_destroy = false
      recover_soft_deleted_key_vaults    = true
    }
    resource_group {
      prevent_deletion_if_contains_resources = true
    }
  }
}

# Uses the tenant you're signed in to with `az login` (the operator's home tenant).
provider "azuread" {}

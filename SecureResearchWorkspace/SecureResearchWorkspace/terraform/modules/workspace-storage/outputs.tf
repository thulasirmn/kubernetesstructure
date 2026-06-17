output "storage_account_name" {
  value = azurerm_storage_account.workspace.name
}

output "file_share_name" {
  value = azurerm_storage_share.workspace.name
}

output "account_key" {
  value     = azurerm_storage_account.workspace.primary_access_key
  sensitive = true
}

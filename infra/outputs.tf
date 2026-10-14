output "droplet_ipv4" {
  description = "IP público de cada ambiente (alvo do SSH do deploy.yml)."
  value       = { for name, droplet in digitalocean_droplet.app : name => droplet.ipv4_address }
}

output "site_address" {
  description = "Endereço público (SITE_ADDRESS do Caddy) de cada ambiente."
  value       = { for name, env in var.environments : name => env.subdomain == "@" ? var.domain : "${env.subdomain}.${var.domain}" }
}

output "production_database_private_host" {
  description = "Host privado (VPC) do PostgreSQL gerenciado de produção."
  value       = digitalocean_database_cluster.production.private_host
}

output "production_database_uri" {
  description = "URI privada do banco de produção (vai para o GitHub Environment secret, nunca para logs)."
  value       = digitalocean_database_cluster.production.private_uri
  sensitive   = true
}

output "artifacts_bucket" {
  description = "Bucket do Spaces para artefatos e backups extras."
  value       = digitalocean_spaces_bucket.artifacts.bucket_domain_name
}

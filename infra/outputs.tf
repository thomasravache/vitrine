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

output "production_database_connection_string" {
  description = "Connection string Npgsql (chave=valor) do usuário de aplicação vitrine_app no banco vitrine, pela VPC e com TLS — valor de DB_CONNECTION_STRING no GitHub Environment production; nunca o doadmin, nunca em logs."
  value = join(";", [
    "Host=${digitalocean_database_cluster.production.private_host}",
    "Port=${digitalocean_database_cluster.production.port}",
    "Database=${digitalocean_database_db.vitrine.name}",
    "Username=${digitalocean_database_user.app.name}",
    "Password=${digitalocean_database_user.app.password}",
    "SSL Mode=Require",
  ])
  sensitive = true
}

output "artifacts_bucket" {
  description = "Bucket do Spaces para artefatos e backups extras."
  value       = digitalocean_spaces_bucket.artifacts.bucket_domain_name
}

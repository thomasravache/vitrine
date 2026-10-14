# SPEC-0005 — infraestrutura do Vitrine no DigitalOcean (ADR-0004, ADR-0010, ADR-0012).
# staging: droplet pequeno com Postgres em container. production: droplet + PostgreSQL gerenciado (PITR).

locals {
  env_tags = { for name in keys(var.environments) : name => "env:${name}" }
}

resource "digitalocean_tag" "service" {
  name = "service:vitrine"
}

resource "digitalocean_tag" "env" {
  for_each = local.env_tags
  name     = each.value
}

resource "digitalocean_vpc" "main" {
  name     = "vitrine-${var.region}"
  region   = var.region
  ip_range = "10.40.0.0/20"
}

resource "digitalocean_ssh_key" "deploy" {
  for_each   = var.deploy_ssh_public_keys
  name       = "vitrine-deploy-${each.key}"
  public_key = each.value
}

resource "digitalocean_droplet" "app" {
  for_each = var.environments

  name       = "vitrine-${each.key}"
  image      = var.droplet_image
  region     = var.region
  size       = each.value.droplet_size
  vpc_uuid   = digitalocean_vpc.main.id
  ipv6       = true
  monitoring = true
  ssh_keys   = contains(keys(digitalocean_ssh_key.deploy), each.key) ? [digitalocean_ssh_key.deploy[each.key].fingerprint] : []
  tags       = [digitalocean_tag.service.id, digitalocean_tag.env[each.key].id]

  user_data = templatefile("${path.module}/cloud-init.yaml", {
    deploy_ssh_public_key = lookup(var.deploy_ssh_public_keys, each.key, "")
  })

  lifecycle {
    # Recriar o servidor é decisão explícita (runbook), nunca efeito colateral de mudar o cloud-init.
    ignore_changes = [user_data, image]
  }
}

resource "digitalocean_firewall" "app" {
  name        = "vitrine-app"
  droplet_ids = [for droplet in digitalocean_droplet.app : droplet.id]

  inbound_rule {
    protocol         = "tcp"
    port_range       = "22"
    source_addresses = var.ssh_allowed_cidrs
  }

  inbound_rule {
    protocol         = "tcp"
    port_range       = "80"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  inbound_rule {
    protocol         = "tcp"
    port_range       = "443"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  inbound_rule {
    protocol         = "udp"
    port_range       = "443"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  inbound_rule {
    protocol         = "icmp"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "tcp"
    port_range            = "1-65535"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "udp"
    port_range            = "1-65535"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "icmp"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }
}

# PostgreSQL gerenciado de produção: backups diários + PITR de 7 dias inclusos (ADR-0010).
resource "digitalocean_database_cluster" "production" {
  name                 = "vitrine-production-pg"
  engine               = "pg"
  version              = var.postgres_version
  size                 = var.postgres_size
  region               = var.region
  node_count           = 1
  private_network_uuid = digitalocean_vpc.main.id
  tags                 = [digitalocean_tag.service.id, digitalocean_tag.env["production"].id]
}

resource "digitalocean_database_db" "vitrine" {
  cluster_id = digitalocean_database_cluster.production.id
  name       = "vitrine"
}

resource "digitalocean_database_user" "app" {
  cluster_id = digitalocean_database_cluster.production.id
  name       = "vitrine_app"
}

# Só o droplet de produção alcança o banco gerenciado.
resource "digitalocean_database_firewall" "production" {
  cluster_id = digitalocean_database_cluster.production.id

  rule {
    type  = "droplet"
    value = digitalocean_droplet.app["production"].id
  }
}

resource "digitalocean_domain" "main" {
  name = var.domain
}

resource "digitalocean_record" "app_ipv4" {
  for_each = var.environments

  domain = digitalocean_domain.main.id
  type   = "A"
  name   = each.value.subdomain
  value  = digitalocean_droplet.app[each.key].ipv4_address
  ttl    = 300
}

resource "digitalocean_record" "app_ipv6" {
  for_each = var.environments

  domain = digitalocean_domain.main.id
  type   = "AAAA"
  name   = each.value.subdomain
  value  = digitalocean_droplet.app[each.key].ipv6_address
  ttl    = 300
}

resource "digitalocean_spaces_bucket" "artifacts" {
  name   = var.spaces_bucket_name
  region = var.region
  acl    = "private"

  versioning {
    enabled = true
  }
}

resource "digitalocean_project" "vitrine" {
  name        = "vitrine"
  description = "Vitrine — staging e produção (SPEC-0005/SPEC-0009)."
  purpose     = "Web Application"
  environment = "Production"
  resources = concat(
    [for droplet in digitalocean_droplet.app : droplet.urn],
    [
      digitalocean_database_cluster.production.urn,
      digitalocean_domain.main.urn,
      digitalocean_spaces_bucket.artifacts.urn,
    ],
  )
}

variable "region" {
  description = "Região do DigitalOcean (NYC: latência para a API do Mercado Livre, ADR-0004)."
  type        = string
  default     = "nyc3"
}

variable "domain" {
  description = "Domínio público gerenciado no DNS do DigitalOcean (definido na SPEC-0009)."
  type        = string
  default     = "vitrine.example.com"
}

variable "droplet_image" {
  description = "Imagem base dos droplets."
  type        = string
  default     = "ubuntu-24-04-x64"
}

variable "environments" {
  description = "Ambientes e o tamanho do droplet de cada um (ADR-0012: staging US$ 12, production US$ 24)."
  type = map(object({
    droplet_size = string
    subdomain    = string
  }))
  default = {
    staging = {
      droplet_size = "s-1vcpu-2gb"
      subdomain    = "staging"
    }
    production = {
      droplet_size = "s-2vcpu-4gb"
      subdomain    = "@"
    }
  }

  validation {
    condition     = alltrue([for name in keys(var.environments) : contains(["staging", "production"], name)])
    error_message = "Ambientes suportados: staging e production."
  }
}

variable "deploy_ssh_public_keys" {
  description = "Chave pública SSH dedicada ao deploy de cada ambiente (a privada fica no GitHub Environment secret)."
  type        = map(string)
  default     = {}
}

variable "ssh_allowed_cidrs" {
  description = "Origens autorizadas a acessar SSH (porta 22) nos droplets."
  type        = list(string)
  default     = ["0.0.0.0/0", "::/0"]
}

variable "postgres_version" {
  description = "Versão do PostgreSQL gerenciado (maior suportada >= 17, ADR-0011)."
  type        = string
  default     = "17"
}

variable "postgres_size" {
  description = "Tamanho do PostgreSQL gerenciado de produção (ADR-0012: desde US$ 15)."
  type        = string
  default     = "db-s-1vcpu-1gb"
}

variable "spaces_bucket_name" {
  description = "Bucket do Spaces para artefatos de release e backups extras."
  type        = string
  default     = "vitrine-artifacts"
}

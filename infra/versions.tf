# SPEC-0005 — OpenTofu (ADR-0004). Validado (tofu validate + tflint); NUNCA aplicado nesta spec:
# o provisionamento real é da SPEC-0009.
terraform {
  required_version = ">= 1.10.0"

  required_providers {
    digitalocean = {
      source  = "digitalocean/digitalocean"
      version = "~> 2.0"
    }
  }

  # Estado remoto no DigitalOcean Spaces (compatível com S3), com trava nativa por arquivo (.tflock).
  # Credenciais por ambiente: AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY = chave de acesso do Spaces.
  # O bucket de estado é criado à mão uma única vez (docs/runbooks/recriar-servidor.md).
  backend "s3" {
    bucket       = "vitrine-tofu-state"
    key          = "vitrine/terraform.tfstate"
    region       = "us-east-1"
    use_lockfile = true
    encrypt      = true
    endpoints = {
      s3 = "https://nyc3.digitaloceanspaces.com"
    }
    skip_credentials_validation = true
    skip_requesting_account_id  = true
    skip_metadata_api_check     = true
    skip_region_validation      = true
    skip_s3_checksum            = true
    use_path_style              = false
  }
}

# Token em DIGITALOCEAN_TOKEN; chaves do Spaces em SPACES_ACCESS_KEY_ID / SPACES_SECRET_ACCESS_KEY.
provider "digitalocean" {}

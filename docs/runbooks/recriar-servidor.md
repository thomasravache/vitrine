# Runbook — Recriar servidor

> SPEC-0005 escreve a infraestrutura; **nada é aplicado** até a SPEC-0009. Este runbook vale a partir do provisionamento.
> RTO alvo: ≤ 4 h (ADR-0010).

## Pré-requisitos
- OpenTofu ≥ 1.10, token do DigitalOcean (`DIGITALOCEAN_TOKEN`), chave de acesso do Spaces (`SPACES_ACCESS_KEY_ID`, `SPACES_SECRET_ACCESS_KEY`
  e as mesmas em `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` para o backend de estado).
- Bucket de estado `vitrine-tofu-state` (nyc3) criado uma única vez à mão no painel (o estado não pode morar num bucket que ele mesmo cria).
- Chaves públicas de deploy por ambiente em `infra/<arquivo>.tfvars` (git-ignored): `deploy_ssh_public_keys = { staging = "...", production = "..." }`.

## Validar (sem custo)
```bash
bash tests/infra/run.sh UT-01 IT-06   # tofu validate + tflint; cloud-init num container
```

## Recriar um droplet
```bash
cd infra
tofu init
tofu plan  -var-file=prod.tfvars -replace='digitalocean_droplet.app["production"]'
tofu apply -var-file=prod.tfvars -replace='digitalocean_droplet.app["production"]'
```
O cloud-init instala Docker + Compose, cria o usuário `deploy` (só chave SSH, sem senha, sem root), liga `unattended-upgrades`, swap de 2 GB e fail2ban,
e cria `/opt/vitrine`. Mudanças em `cloud-init.yaml` não recriam droplets sozinhas (`ignore_changes`): recriar é sempre este procedimento explícito.

Depois:
1. Atualize `DEPLOY_HOST` e `DEPLOY_KNOWN_HOSTS` (`ssh-keyscan <ip>`) no GitHub Environment; o DNS segue o IP pelo OpenTofu.
2. Rode o workflow `Deploy` com a última tag boa (`deploy/.state/current` do servidor antigo ou o último deploy verde).
3. O certificado é reemitido pelo Caddy automaticamente; confira `https://<SITE_ADDRESS>/health/live`.
4. Banco de produção é gerenciado e não é afetado; staging recria o Postgres em container (dados descartáveis).

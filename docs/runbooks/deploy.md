# Runbook — Deploy

> SPEC-0005. Até a SPEC-0009 o único alvo é o **ambiente local prod-like**; o workflow `deploy.yml` está desligado (`DEPLOY_ENABLED`).

## Como funciona
`deploy/deploy.sh <tag> [--env-file <arquivo>]`, igual no ambiente local e no servidor:

1. grava `deploy/.env` (permissão 600, nunca versionado) a partir do `--env-file` (ou do `deploy/.env` atual) + `IMAGE_TAG=<tag>`;
2. `docker compose pull --policy missing` — só baixa do registry o que não existe localmente;
3. `docker compose run --rm migrate` — a mesma imagem com `--migrate` (ADR-0011). Falhou: sai 1 **sem trocar a versão**;
4. `docker compose up -d` com a nova tag e espera `GET https://<SITE_ADDRESS>/health/live` = 200 (até 120 s; desiste antes se a API reiniciar 3 vezes). Falhou: volta sozinho para a tag anterior e sai 1;
5. grava a tag em `deploy/.state/current` e a anterior em `deploy/.state/previous`.

## Ambiente local prod-like
Pré-requisitos: Docker com Compose v2, portas 80 e 443 livres, .NET SDK (para construir a imagem).

```bash
# 1. Imagem (ou use ghcr.io/thomasravache/vitrine-api:<sha> com IMAGE_REPO no .env)
#    PublishSpa=true embute web/dist em wwwroot; sem ele a imagem não serve a SPA (rotas como /cadastro dão 401).
TAG=$(git rev-parse --short HEAD)
npm --prefix web ci && npm --prefix web run build
dotnet publish src/Api/Vitrine.Api.csproj -c Release -t:PublishContainer -p:PublishSpa=true -p:ContainerRepository=vitrine-api -p:ContainerImageTag=$TAG

# 2. Configuração (dados sintéticos)
cp deploy/local/.env.example deploy/local/.env

# 3. Deploy
deploy/deploy.sh $TAG --env-file deploy/local/.env

# 4. Conferir (CA interna do Caddy: -k; *.localhost pode não resolver no seu sistema)
curl -fsSk --resolve vitrine.localhost:443:127.0.0.1 https://vitrine.localhost/health/live
docker compose -p vitrine ps
```

Para o navegador confiar no certificado local, exporte a raiz da CA interna com
`docker compose -p vitrine cp caddy:/data/caddy/pki/authorities/local/root.crt .` e importe-a no sistema/navegador. Se `vitrine.localhost`
não resolver, adicione `127.0.0.1 vitrine.localhost` ao `/etc/hosts`.

Derrubar tudo (mantém os volumes; `-v` apaga o banco local):
```bash
docker compose -p vitrine down
```

## Servidor (a partir da SPEC-0009)
- Automático: CI verde na `main` → `deploy.yml` → staging. Manual: *Run workflow* com `environment` e `tag` (SHA); produção exige aprovação no Environment `production`.
- O workflow resolve imagem e `compose-bundle` com `deploy/resolve-release.sh <sha>`, envia `deploy/` para `/opt/vitrine/deploy` e roda `deploy.sh` por SSH com o `.env` vindo de `secrets.DEPLOY_ENV_FILE`.
- Manual no servidor (emergência): `ssh deploy@<host>` → `cd /opt/vitrine && deploy/deploy.sh <sha>` (reaproveita o `.env` atual).

## Variáveis do `.env`
| Variável | Uso |
|---|---|
| `IMAGE_REPO` | `vitrine-api` (local) ou `ghcr.io/thomasravache/vitrine-api` |
| `SITE_ADDRESS` | host público (Caddy + health check) |
| `DB_CONNECTION_STRING` | connection string Npgsql do PostgreSQL (`ConnectionStrings__vitrine`). Produção: `tofu output -raw production_database_connection_string` (usuário `vitrine_app`, banco `vitrine`, VPC + TLS; nunca o `doadmin`) |
| `COMPOSE_PROFILES` | `db-container` sobe o Postgres em container (local e staging) |
| `POSTGRES_PASSWORD` | senha do Postgres em container |
| `DEPLOY_HEALTH_INSECURE` | `true` só onde o certificado não é público (local) |
| `DEPLOY_HEALTH_TIMEOUT` | segundos de espera do health (≤ 120) |
| `DEPLOY_COMPOSE_BASE` | base do Compose alternativa (ex.: `docker-compose.yaml` do `compose-bundle`) |
| `FEATURES_SIGNUP_OPEN` | cadastro público (`Features__SignupOpen`): `true` no local prod-like e em staging; **`false` em produção até o lançamento** (padrão `false` se ausente; com `false`, `POST /api/v1/auth/register` responde 404) |
| `ASPNETCORE_ENVIRONMENT` | sempre `Production` fora do desenvolvimento local: `Development` aplica migrações na subida da API e nunca é usado em servidor |

## Pendências para a SPEC-0009
- **Versão do PostgreSQL gerenciado:** `postgres_version` está em `17` (padrão em `infra/variables.tf`); confirmar a maior versão oferecida pelo DigitalOcean (ADR-0011: ≥ 17, preferir 18 como no ambiente local) antes do provisionamento.
- **Uso do `compose-bundle`:** o deploy usa a base `deploy/compose.yaml` do repositório; o artefato `compose-bundle` do CI (`aspire publish`) só é localizado por `resolve-release.sh`. Adotá-lo como base (`DEPLOY_COMPOSE_BASE`) exige alinhar nomes de serviço e variáveis (`api`, `IMAGE_REPO`, `IMAGE_TAG`).

## Diagnóstico
- `deploy: migração falhou` → `docker compose -p vitrine run --rm migrate` e leia o log; a versão anterior segue no ar.
- `health check ... falhou` → `docker compose -p vitrine logs api --tail 100`; a versão anterior já foi restaurada.
- O script nunca imprime o `.env`; não use `docker compose config` em logs compartilhados (mostra segredos).

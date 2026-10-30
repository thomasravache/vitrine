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
#    Simulador do Mercado Livre (SPEC-0024; perfil ml-simulator, ligado no .env.example): imagem só local.
dotnet publish tools/MercadoLivreSimulator/Vitrine.MercadoLivreSimulator.csproj -c Release -t:PublishContainer -p:ContainerRepository=vitrine-ml-simulator -p:ContainerImageTag=local

# 2. Configuração (dados sintéticos; Api apontada para o simulador)
cp deploy/local/.env.example deploy/local/.env

# 3. Deploy
deploy/deploy.sh $TAG --env-file deploy/local/.env

# 4. Conferir (CA interna do Caddy: -k; *.localhost pode não resolver no seu sistema)
curl -fsSk --resolve vitrine.localhost:443:127.0.0.1 https://vitrine.localhost/health/live
curl -fsSk --resolve ml-simulador.vitrine.localhost:443:127.0.0.1 https://ml-simulador.vitrine.localhost/health
docker compose -p vitrine ps
```

### Simulador do Mercado Livre no ambiente local (SPEC-0024, ADR-0016)
O `deploy/local/.env.example` liga o perfil `ml-simulator` e aponta a Api para o simulador, para testar a jornada
"conectar conta" em `https://vitrine.localhost` sem conta nem app no ML:

- o navegador abre a autorização em `https://ml-simulador.vitrine.localhost` (Caddy, mesma CA interna; adicione o host ao
  `/etc/hosts` se `*.localhost` não resolver) — a página se identifica como "Simulador do Mercado Livre — ambiente de teste";
- a Api fala com ele pela rede interna (`ML_API_BASE_URL=http://ml-simulator:8080`, aceito em Production só com
  `ML_ALLOW_NON_OFFICIAL_ENDPOINTS=true`) e recebe as notificações do simulador no webhook `/api/v1/mercadolivre/notifications`
  (serviço `api`, porta interna 8080);
- `ML_CLIENT_ID`/`ML_CLIENT_SECRET` do exemplo são sintéticos e públicos, usados pelos dois lados; nunca coloque ali os do
  app real do DevCenter;
- o site do Caddy vem de `deploy/local/caddy-sites/` (`CADDY_EXTRA_SITES_DIR`); sem a variável, o Caddy importa
  `deploy/caddy-sites/` (vazio) — staging e produção nunca ligam o perfil nem a variável;
- estado só em memória: reiniciar o container volta à massa inicial (`VENDEDOR_TESTE`, `VENDEDOR_AR`); `POST /__sim/*`
  controla vendedores, anúncios, tokens e notificações (contrato na SPEC-0024, seção 6).

**Quem já tem `deploy/local/.env`:** copie do `.env.example` as linhas novas (`COMPOSE_PROFILES`, `CADDY_EXTRA_SITES_DIR`,
`ML_*`) e construa a imagem do simulador (passo 1) antes do próximo deploy — com o perfil ligado e sem a imagem
`vitrine-ml-simulator:local`, o `docker compose up` falha (a imagem nunca é baixada de registry). Para desligar o
simulador, tire `ml-simulator` de `COMPOSE_PROFILES`, remova `CADDY_EXTRA_SITES_DIR` e volte `ML_*` para vazio/`false`.

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
| `COMPOSE_PROFILES` | `db-container` sobe o Postgres em container (local e staging); `ml-simulator` sobe o simulador do ML (**só local**) |
| `CADDY_EXTRA_SITES_DIR` | diretório de sites extras do Caddy (`*.caddy`); só o local usa (`./local/caddy-sites`); padrão `deploy/caddy-sites` (vazio) |
| `ML_*` | Mercado Livre (SPEC-0020): no local, o simulador com credenciais sintéticas; em servidor, o app do DevCenter (secrets) e URLs oficiais |
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

## Smoke no ambiente local prod-like (CA interna do Caddy)

O E2E faz chamadas pelo Node (controle `/__sim` do simulador) e valida o certificado. Exporte a raiz da CA local do
Caddy e aponte o Node para ela (o `smoke_test` do `docs/specs/sdd-config.yml` já faz isso quando `BASE_URL` é
`*.localhost`):

```bash
docker compose -p vitrine cp caddy:/data/caddy/pki/authorities/local/root.crt /tmp/vitrine-caddy-root.crt
NODE_EXTRA_CA_CERTS=/tmp/vitrine-caddy-root.crt BASE_URL=https://vitrine.localhost \
  ML_SIMULATOR_URL=https://ml-simulador.vitrine.localhost npm --prefix web run e2e -- --grep @smoke
```

A raiz é gerada pelo Caddy no primeiro start e fica no volume `/data`; se o volume for recriado, exporte de novo. Sem
`NODE_EXTRA_CA_CERTS`, a jornada `conectar-conta` falha com `unable to get local issuer certificate`. Staging e produção
usam certificados públicos e não definem `ML_SIMULATOR_URL` (a jornada `conectar-conta` é pulada com motivo).

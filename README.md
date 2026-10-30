# Vitrine

Assistente para vendedores do Mercado Livre. O Vitrine **melhora anúncios** (título, descrição, ficha técnica)
sugerindo mudanças que o vendedor compara e **aprova** antes de publicar, e **responde perguntas** de compradores
— com aprovação ou automaticamente, em até 2 minutos. Histórico e rollback de cada mudança aplicada.

Produto web, mobile-first (sem app nas lojas). Especificações em [`docs/specs/`](docs/specs/INDEX.md);
decisões de arquitetura em [`docs/adr/`](docs/adr/).

## Stack

- **Backend:** .NET 10 (LTS), monólito modular ([ADR-0001](docs/adr/ADR-0001-arquitetura-e-fronteiras.md)),
  orquestrado por Aspire 13; PostgreSQL.
- **Frontend:** SPA React 19 + Vite + TypeScript estrito + Tailwind CSS
  ([ADR-0013](docs/adr/ADR-0013-frontend-spa-react-com-vite.md)).
- **Qualidade:** `dotnet format` + .NET analyzers (warnings são erros), Biome + `tsc --noEmit`
  ([ADR-0003](docs/adr/ADR-0003-qualidade-de-codigo.md)).

## Pré-requisitos

- .NET SDK 10.0.4xx (fixado em `global.json`)
- Node.js 24 LTS + npm
- Docker (PostgreSQL local e testes de integração)
- [Aspire CLI](https://aspire.dev) 13.x (`aspire run`)

## Como rodar

```bash
git config core.hooksPath .githooks   # uma vez por clone: ativa os hooks de commit
npm --prefix web ci                   # dependências da SPA (lockfile)
aspire run                            # sobe postgres, api e web; abre o dashboard do Aspire
```

Sem o Aspire CLI: `dotnet run --project src/AppHost`.

O lockfile do AppHost (`src/AppHost/packages.lock.json`) fixa os pacotes DCP/dashboard de **linux-x64**
(plataforma do desenvolvimento e do CI); em outro sistema operacional a restauração reescreve o lockfile — não o versione.

O AppHost sobe o PostgreSQL (container), a API e o Vite com HMR. O dashboard do Aspire mostra
logs, traces, métricas e o endereço de cada recurso. A API responde `GET /health/live`.

## O que já funciona

- **Conta (SPEC-0006):** cadastro em `/cadastro`, login em `/entrar`, painel vazio em `/painel` e logout, no celular e no
  desktop. O cadastro só abre com `Features:SignupOpen=true` (variável `FEATURES_SIGNUP_OPEN` no deploy): ligado no
  desenvolvimento e no ambiente local, desligado em produção até o lançamento. API em `/api/v1/auth/*` e `/api/v1/me`;
  módulo de referência para os próximos: `src/Modules/Identity/`.
- **Observabilidade (SPEC-0007):** `GET /health/live` e `GET /health/ready` (inclui o banco), métricas
  `vitrine.auth.*`, traces com spans de banco e logs mascarados no dashboard do Aspire. Fora do desenvolvimento os logs
  saem em JSON com `trace_id`. Grafana Cloud e Sentry só ligam com as variáveis `OTEL_EXPORTER_OTLP_*`, `SENTRY_DSN`,
  `GRAFANA_CLOUD_*` e o perfil `observability` do Compose (ver `deploy/local/.env.example`); alertas em
  `deploy/alloy/grafana/` e um runbook por alerta em `docs/runbooks/`.
- **Segurança (SPEC-0008):** toda rota exige login, exceto cadastro, login, token antiforgery, health checks e a SPA
  (teste de arquitetura barra endpoint anônimo novo). Cabeçalhos de segurança (CSP estrita, HSTS fora do
  desenvolvimento), limite de 10/min por IP no cadastro/login e 10/min por e-mail no login (429), e cifra de campos
  sensíveis com `IFieldProtector` / `IsProtected("<propósito>")` em `src/Common/Vitrine.Common.Security`. As chaves do
  Data Protection ficam no PostgreSQL, cifradas pela chave mestra `DATA_PROTECTION_MASTER_KEY` (obrigatória fora do
  desenvolvimento; rotação em `docs/runbooks/rotacao-chave-mestra.md`).
- **Contas do Mercado Livre (SPEC-0020):** o dono do workspace conecta e desconecta contas do ML (MLB) pelo OAuth
  oficial com PKCE (`/api/v1/mercadolivre/connections`, `/connections/complete`, `/accounts`); tokens cifrados que
  nunca chegam à SPA. Sem `MercadoLivre__ClientId`/`MercadoLivre__ClientSecret` a API responde 503
  `ML_NOT_CONFIGURED`. Módulo em `src/Modules/MercadoLivre/`; filtros web compartilhados em
  `src/Common/Vitrine.Common.Web`.
- **Anúncios (SPEC-0022):** os anúncios das contas conectadas ficam numa cópia local (`src/Modules/Listings/`),
  importada ao conectar, atualizada por notificação e reconciliada a cada 24 h; API `GET /api/v1/listings`
  (busca, filtros, cursor), `GET /api/v1/listings/summary` e `POST /api/v1/listings/sync` (no máximo a cada 5 min).

## Comandos

Todos na raiz do repositório.

| Objetivo | Comando |
|---|---|
| Build (sem avisos) | `dotnet build Vitrine.slnx -warnaserror && npm --prefix web ci && npm --prefix web run build` |
| Lint / formatação (verificação) | `dotnet format Vitrine.slnx --verify-no-changes && npm --prefix web run lint` |
| Corrigir formatação | `dotnet format Vitrine.slnx` e `npm --prefix web run lint:fix` |
| Testes unitários | `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` |
| Testes de integração (Docker) | `dotnet test tests/Vitrine.IntegrationTests` |
| Testes de arquitetura | `dotnet test tests/Vitrine.ArchitectureTests` |
| E2E (Playwright, sobe o AppHost) | `npm --prefix web run e2e` (com `BASE_URL` roda contra um ambiente) |
| Suíte .NET completa | `dotnet test --solution Vitrine.slnx` |
| Cobertura | comando `coverage` de `docs/specs/sdd-config.yml` (diff-cover ≥ 80% obrigatório no CI) |
| Testes de infra e deploy | `bash tests/infra/run.sh` (portas 80/443 livres) |
| Deploy local prod-like | `cp deploy/local/.env.example deploy/local/.env && deploy/deploy.sh <tag> --env-file deploy/local/.env` → `https://vitrine.localhost` (ver `docs/runbooks/deploy.md`) |
| Smoke contra o ambiente local | `BASE_URL=https://vitrine.localhost npm --prefix web run e2e -- --grep @smoke` |
| Regenerar o cliente da API (orval) | `npm --prefix web run api:generate` (versione `web/src/api`; sem banco nem chave mestra; o CI reprova o PR se o cliente estiver desatualizado) |
| SPA isolada | `npm --prefix web run dev` |
| Estado das specs | `python3 tools/sdd/spec_graph.py status` |

## Mapa de pastas

```text
Vitrine.slnx                 solução (.slnx)
global.json                  SDK .NET fixado; testes no Microsoft Testing Platform
Directory.Build.props        regras de build: nullable, warnings como erro, analyzers, lockfiles
Directory.Packages.props     versões NuGet centralizadas (Central Package Management)
src/
  AppHost/                   Aspire: orquestra postgres, api e web
  ServiceDefaults/           OpenTelemetry, health checks, resiliência HTTP
  Api/                       host da aplicação (compõe os módulos)
  Common/                    building blocks sem regra de negócio
  Modules/<Modulo>/          Domain, Application, Infrastructure, Presentation, IntegrationEvents
web/                         SPA React + Vite (Biome, Tailwind)
tests/                       projetos de teste (xUnit v3)
tools/sdd/                   ferramenta do processo SDD (spec_graph.py)
docs/specs/                  specs, índice e configuração SDD
docs/adr/                    decisões de arquitetura
.githooks/                   hooks de commit (commit-msg, pre-commit)
.github/                     CODEOWNERS e template de PR
```

## Convenções

- **Processo:** Spec-Driven Development — nenhuma mudança de comportamento sem spec aprovada; ver
  [`AGENTS.md`](AGENTS.md).
- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/pt-br/v1.0.0/) com rodapé
  `Refs: SPEC-NNNN`, verificados pelo hook `commit-msg`:

  ```text
  feat(listings): sugere novo título para o anúncio

  Refs: SPEC-0012
  ```

  Mudança sem comportamento (typo, formatação) pode usar `[no-spec]` no assunto.
- **Changelog:** [`CHANGELOG.md`](CHANGELOG.md) no formato [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/).
- **Versões:** [SemVer](https://semver.org/lang/pt-BR/).
- **Fluxo de mudança** ([ADR-0005](docs/adr/ADR-0005-fluxo-de-mudanca.md)): toda mudança entra na `main` por PR
  com checks obrigatórios verdes e review; merge commit (sem squash); sem push direto nem force-push na `main`.
  Arquivos de controle (`tools/sdd`, `docs/adr`, `docs/specs/sdd-config.yml`, `.githooks`, `.github/workflows`)
  têm dono obrigatório em [`.github/CODEOWNERS`](.github/CODEOWNERS).
- **Dependências** ([ADR-0008](docs/adr/ADR-0008-dependencias-e-ciclo-de-vida-da-stack.md)): versões NuGet só em
  `Directory.Packages.props`; lockfiles (`packages.lock.json`, `web/package-lock.json`) sempre versionados.
- **Segredos:** nunca no repositório; `.env*` e `appsettings.*.local.json` são ignorados pelo git.

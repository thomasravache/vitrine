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

## Comandos

Todos na raiz do repositório.

| Objetivo | Comando |
|---|---|
| Build (sem avisos) | `dotnet build Vitrine.slnx -warnaserror && npm --prefix web ci && npm --prefix web run build` |
| Lint / formatação (verificação) | `dotnet format Vitrine.slnx --verify-no-changes && npm --prefix web run lint` |
| Corrigir formatação | `dotnet format Vitrine.slnx` e `npm --prefix web run lint:fix` |
| Testes | `dotnet test --solution Vitrine.slnx` (Docker necessário para os de integração) |
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

<!-- ci-proof IT-01 (SPEC-0004): mudança trivial, PR fechado sem merge -->

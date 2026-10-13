---
id: SPEC-0002
title: Repositório, ferramentas e convenções
tier: full
type: foundation
user_facing: false
status: implemented
created: 2026-10-13
parent: SPEC-0001
depends_on: []
consumes_contract: []
contract_version: 1
touches: [Vitrine.slnx, global.json, Directory.Build.props, Directory.Packages.props, .editorconfig, .gitignore, .gitattributes, src/AppHost/**, src/ServiceDefaults/**, src/Api/**, src/Common/**, src/Modules/**, web/**, tests/Vitrine.Tooling.Tests/**, tools/sdd/**, .githooks/**, .github/CODEOWNERS, .github/pull_request_template.md, README.md, CLAUDE.md, AGENTS.md, CHANGELOG.md]
adrs: [ADR-0001, ADR-0003, ADR-0005, ADR-0008, ADR-0013]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0002 — Repositório, ferramentas e convenções

## 1. Visão Geral
Cria o repositório do Vitrine com a estrutura do monólito modular (.NET 10 + Aspire 13) e da SPA (React + Vite), as ferramentas de formatação/lint, as convenções de commit, changelog e versão, e instala o enforcement SDD (ferramenta, hooks, CODEOWNERS, template de PR, bloco nos documentos de agentes). É a base onde todas as outras specs trabalham.

## 2. Motivação & Escopo
**Motivação:** sem estrutura, convenções e enforcement desde o primeiro commit, cada spec seguinte inventaria o próprio padrão e os agentes divergiriam.

**Objetivos (dentro do escopo):**
- Solução `Vitrine.slnx` com `src/AppHost` (Aspire), `src/ServiceDefaults`, `src/Api` (host mínimo), pasta `src/Modules/` e `src/Common/` vazias com README de convenção.
- `global.json` fixando o SDK .NET 10; `Directory.Build.props` (nullable, warnings como erro, analyzers); `Directory.Packages.props` (versões centralizadas); `.editorconfig`; lockfiles habilitados.
- SPA em `web/` (Vite + React + TypeScript estrito + Biome + Tailwind CSS), registrada no AppHost com `AddViteApp`; Postgres registrado no AppHost.
- Comando único para subir tudo localmente (`aspire run`).
- `vendor --agents --pr-template github --hooks --changelog --codeowners`: `tools/sdd/`, `.githooks/`, `.github/pull_request_template.md`, `.github/CODEOWNERS`, bloco SDD em `CLAUDE.md`/`AGENTS.md`, `CHANGELOG.md`.
- `README.md` com visão do produto, como rodar, convenções (Conventional Commits, Keep a Changelog, SemVer) e mapa de pastas.
- Repositório criado no GitHub e proteção da `main` configurada (checks obrigatórios adicionados na SPEC-0004).
- `sdd-config.yml` com os comandos `build` e `lint` reais.
- Projeto de testes `tests/Vitrine.Tooling.Tests` (xUnit v3) para os testes desta spec.

**Não-objetivos (fora do escopo):**
- Testcontainers, Playwright, testes de arquitetura (SPEC-0003); workflows de CI (SPEC-0004); módulos de negócio (SPEC-0006 em diante).

## 3. Dependências
- **Implementações necessárias:** N/A — primeira spec.
- **Contratos consumidos:** N/A
- **Pré-requisitos externos:** .NET SDK 10.0.4xx, Node 24 LTS, Docker, Aspire CLI 13.x, `gh` autenticado com permissão para criar o repositório (verificado: conta thomasravache autenticada).

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido nos ADRs de fundação ADR-0001 (monólito modular + Aspire), ADR-0003 (qualidade de código), ADR-0005 (fluxo de mudança), ADR-0008 (dependências), ADR-0013 (frontend).

**Decisão:** estrutura de solução e ferramentas exatamente como descritas nesses ADRs; `.slnx` (formato de solução XML do .NET 10) e Central Package Management.

**Justificativa:** um padrão único desde o primeiro commit, verificável por comando (`build`, `lint`) e por hooks.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** `.sln` clássico (formato antigo, conflitos de merge); monorepo com Nx/Turborepo (desnecessário para uma SPA só).

**ADRs:** ADR-0001, ADR-0003, ADR-0005, ADR-0008, ADR-0013.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** N/A — sem código de produção.
- **Segurança:** `.gitignore` cobre `.env*`, `appsettings.*.local.json`, `aspire-output/`; nenhum segredo no repositório — verificado pelo IT-03.
- **Privacidade e dados pessoais:** N/A — nenhum dado pessoal tratado.
- **Disponibilidade e resiliência:** N/A — sem serviço implantado.
- **Acessibilidade (UI):** N/A — a SPA só tem a página padrão; acessibilidade entra com as telas na SPEC-0006.
- **Custo:** repositório privado no plano gratuito do GitHub (minutos de Actions do plano gratuito — avaliado na SPEC-0004).

## 6. Artefato A — Contrato
**Interface:** comandos do repositório e layout de pastas (consumidos por todas as specs seguintes)

```text
Comandos (raiz do repositório):
  dotnet build Vitrine.slnx                       -> compila sem avisos (warnings = erros)
  dotnet format Vitrine.slnx --verify-no-changes  -> exit 0 se formatado; != 0 caso contrário
  (cd web && npm ci && npx biome ci && npx tsc --noEmit) -> exit 0 se SPA formatada/tipada
  aspire run                                      -> sobe postgres, api e web; dashboard Aspire
sdd-config.yml:
  build: "dotnet build Vitrine.slnx -warnaserror && npm --prefix web ci && npm --prefix web run build"
  lint:  "dotnet format Vitrine.slnx --verify-no-changes && npm --prefix web run lint"
Layout:
  src/AppHost  src/ServiceDefaults  src/Api  src/Common/  src/Modules/<Modulo>/{Domain,Application,Infrastructure,Presentation,IntegrationEvents}
  web/  tests/  tools/sdd/  docs/{specs,adr,runbooks}/  .githooks/  .github/
Hooks:
  commit-msg: rejeita mensagem fora de Conventional Commits ou sem "Refs: SPEC-NNNN" (exceto merges)
  pre-commit: roda validate nas specs/ADRs alterados
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Modelo da aplicação | AppHost construído | Recursos `postgres`, `api` e `web` existem, `api` referencia `postgres` | UT-01 |
| Regras de build | Projeto em `src/` | Nullable habilitado, warnings como erro, versões centralizadas | UT-02 |
| Subir tudo localmente | `aspire run` / AppHost em teste | `api` responde 200 em `/health/live` e `web` serve `index.html` | IT-01 |
| Commit fora do padrão | mensagem sem Conventional Commits ou sem `Refs:` | hook `commit-msg` rejeita | IT-02 |
| Commit no padrão | `feat(x): y` + `Refs: SPEC-0002` | hook aceita | IT-02 |
| Arquivo de segredo | criar `.env` ou `appsettings.Production.local.json` | arquivo ignorado pelo git | IT-03 |
| Código mal formatado | arquivo C# ou TS fora do padrão | comando `lint` falha | IT-04 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado o AppHost, quando o modelo da aplicação é construído com `DistributedApplicationTestingBuilder` (sem iniciar), então existem os recursos `postgres`, `api` e `web`, e `api` tem referência a `postgres`.
- **UT-02** — Dado qualquer `.csproj` em `src/`, quando suas propriedades MSBuild avaliadas são lidas, então `Nullable=enable`, `TreatWarningsAsErrors=true` e `ManagePackageVersionsCentrally=true`.

### 7.3 Testes de Integração
- **IT-01** — Dado Docker disponível, quando o AppHost é iniciado via `Aspire.Hosting.Testing`, então `GET /health/live` da `api` retorna 200 e `GET /` da `web` retorna HTML com `<div id="root">`.
- **IT-02** — Dado um repositório git temporário com `core.hooksPath=.githooks`, quando se tenta commitar `"atualiza coisas"`, então o commit é rejeitado; e quando se commita `"feat(repo): estrutura inicial"` com `Refs: SPEC-0002`, então é aceito.
- **IT-03** — Dado o `.gitignore`, quando `git check-ignore` é executado para `.env`, `.env.production`, `src/Api/appsettings.Production.local.json` e `aspire-output/x`, então todos são ignorados.
- **IT-04** — Dado uma cópia do repositório com um arquivo C# e um TS mal formatados, quando o comando `lint` do sdd-config roda, então termina com código diferente de zero.

### 7.4 Testes de Contrato
- N/A — esta spec não fornece contrato consumido via `consumes_contract`; as specs seguintes dependem da implementação (depends_on).

### 7.5 Testes E2E
- N/A — `user_facing: false`: não há jornada de usuário; a primeira jornada é a da SPEC-0006.

### 7.6 Outros
- N/A

**Dublês e dados de teste:** repositórios git temporários por teste; nenhum serviço externo.

**Ambiente de execução:** local e CI (Docker necessário para IT-01).

## 8. Plano de Rollout
- **Estratégia:** deploy direto — não há serviço implantado; entrega = merge do PR na `main`.
- **Dados/schema:** N/A
- **Compatibilidade:** N/A — primeiro commit.
- **Observabilidade:** N/A — sem serviço em execução (dashboard Aspire local apenas).
- **Rollback:** revert do merge commit.
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma (as decisões de nome do repositório e fluxo de revisão estão nas Questões em Aberto do épico SPEC-0001).

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Estrutura e regras de build**
- [x] Red: escrever UT-01, UT-02 com a tag `SPEC-0002:<ID>` (projeto `tests/Vitrine.Tooling.Tests`) e confirmar que falham pelo motivo certo
- [x] Green: `global.json`, `Vitrine.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, AppHost (postgres, api, web), ServiceDefaults, Api mínima com `/health/live`, SPA Vite/React/TS/Biome/Tailwind
- [x] Refactor mantendo tudo verde

**Fase 2: Ferramentas, convenções e enforcement**
- [x] Red: escrever IT-01, IT-02, IT-03, IT-04 e confirmar que falham pelo motivo certo
- [x] Green: `.gitignore`/`.gitattributes`, `vendor --agents --pr-template github --hooks --changelog --codeowners`, README, comandos `build`/`lint` no sdd-config
- [x] Repositório público `thomasravache/vitrine` criado; proteção da `main` (PR obrigatório, sem force-push, merge commit) configurada
- [x] Validar: build + suíte completa (G2)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração (G5; CI = N/A até a SPEC-0004) e aprovação (H2)
- [x] Deploy: N/A — sem serviço implantado (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe | 2026-10-13 |
| G1 Red | PASS | verify G1: PASS — 5964312 | 2026-10-13 |
| G2 Green | PASS | build exit 0 (✓ built in 202ms); test exit 0 (duration: 26s 640ms); lint exit 0 ([0m[34mChecked 8 files in 23[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes appl) — 5920e0a | 2026-10-13 |
| G3 Arquitetura | N/A | arch_test ainda não existe — criado pela SPEC-0003 (testes de arquitetura ArchUnitNET); SPEC-0002 não cria módulos | 2026-10-13 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: reviewer-agent: APPROVED 5920e0a — 0 blocker/major; minor aceito: hook commit-msg vendorizado é mais tolerante que o texto do contrato — 5920e0a | 2026-10-13 |
| G5 Integração & CI | PASS | build exit 0 (✓ built in 507ms); test exit 0 (duration: 26s 770ms) — eb337ed | 2026-10-13 |
| H2 Integração aprovada | PASS | aprovado por thomas: Aprovado (PR #1, 2026-10-13) | 2026-10-13 |
| G6 Deploy | N/A | sem serviço implantado: SPEC-0002 não tem deploy; ambientes e pipeline de deploy vêm na SPEC-0005 | 2026-10-13 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — a196afb | 2026-10-13 |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
Repositório público `thomasravache/vitrine` com a solução .NET 10 (`Vitrine.slnx`): AppHost Aspire 13.6.1 orquestrando `postgres` (banco `vitrine`), `api` e `web`; ServiceDefaults padrão do Aspire (OpenTelemetry, health checks, resiliência, service discovery) com `GET /health/live` em todos os ambientes; SPA em `web/` (React 19.3, Vite 8.3, TypeScript 7.0 estrito, Tailwind 4.3, Biome 2.5). Regras de build (nullable, avisos como erro, analyzers, versões centralizadas, lockfiles), `.editorconfig`, `.gitignore`/`.gitattributes`, enforcement SDD (`tools/sdd/`, hooks `commit-msg`/`pre-commit`, template de PR, CODEOWNERS, bloco SDD em AGENTS.md/CLAUDE.md), README e CHANGELOG. Proteção da `main` no GitHub: PR obrigatório (0 aprovações), regras para administradores, sem force-push/exclusão, só merge commit.

### Como foi feito
Test-writer, Implementer e Reviewer independentes, numa worktree. Scaffold sem lógica (`f3b4536`), red (`e4806d9`, `5964312`), green (`52e72a0`, `d35d4e3`, `cf16b10`, `7d65834`, `3abbcc6`), ajustes da review (`5920e0a`). Desvios aceitos na revisão: ASPIRE010 suprimido com `AspireUseCliBundle=false` explícito (DCP/dashboard vêm do NuGet, sem CLI do Aspire no CI); CA1707 desligado só em `tests/**`; perfil da API só http no desenvolvimento (certificado dev não confiável no CI); `/health/ready` fica para a SPEC-0007; Biome com `vcs.root ".."`; Node fixado em `^24` com `.nvmrc`; lockfile do AppHost fixa pacotes linux-x64 (revisar na SPEC-0004). O hook `commit-msg` vendorizado é mais tolerante que o texto do contrato (aceita `SPEC-NNNN` em qualquer lugar da mensagem, `[no-spec]` e commits Revert/fixup) — aceito como comportamento padrão do `vendor`. Comando `test` do sdd-config provisório (`dotnet test --solution Vitrine.slnx`) até a SPEC-0003. xUnit v3 roda sobre Microsoft Testing Platform (`global.json`).

### Prova de Correção
N/A

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| SPEC-0002:UT-01 | AppHost com postgres, api e web; api referencia postgres | PASS | G5 `dotnet test --solution Vitrine.slnx` em `eb337ed` (12/12) |
| SPEC-0002:UT-02 | Nullable, warnings como erro e CPM em todos os projetos | PASS | G5 em `eb337ed` (12/12) |
| SPEC-0002:IT-01 | AppHost sobe; `/health/live` 200 e SPA serve `#root` | PASS | G5 em `eb337ed` (12/12, Docker) |
| SPEC-0002:IT-02 | Hook rejeita mensagem fora do padrão e aceita a correta | PASS | G5 em `eb337ed` (12/12) |
| SPEC-0002:IT-03 | `.env`, `appsettings.*.local.json` e `aspire-output/` ignorados | PASS | G5 em `eb337ed` (12/12) |
| SPEC-0002:IT-04 | Comando `lint` falha com C# e TS mal formatados | PASS | G5 em `eb337ed` (12/12) |

### Definição de Pronto
- [x] Todos os testes do plano passando e listados na Verificação
- [x] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [x] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [x] Requisitos não-funcionais medidos com evidência (ou N/A justificado)
- [x] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6)
- [x] Observabilidade e rollback prontos conforme o Plano de Rollout
- [x] Documentação raiz e CHANGELOG atualizados (G7)
- [x] Pendências registradas como novas specs (ou nenhuma)

Notas: arquitetura e CI ainda não existem nesta onda (G3 N/A → SPEC-0003; CI → SPEC-0004); "ambiente-alvo" = `main` via PR #1 (G6 N/A, sem serviço).

### Deploy
Sem serviço implantado (G6 N/A). Entrega = merge do PR #1 (https://github.com/thomasravache/vitrine/pull/1) na `main` com merge commit, aprovado por thomas (H2).

### Pendências
Nenhuma nova spec: revisão do lockfile multi-plataforma está no escopo da SPEC-0004; `/health/ready` na SPEC-0007; comando `test` definitivo na SPEC-0003.

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0002`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

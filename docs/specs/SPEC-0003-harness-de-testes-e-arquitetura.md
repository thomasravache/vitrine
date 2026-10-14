---
id: SPEC-0003
title: Harness de testes e arquitetura
tier: full
type: foundation
user_facing: false
status: in-progress
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0002]
consumes_contract: []
contract_version: 2
touches: [web/biome.json, tests/Vitrine.Tooling.Tests/Vitrine.Tooling.Tests.csproj, tests/Vitrine.Tooling.Tests/packages.lock.json, tests/Vitrine.Tooling.Tests/testconfig.json, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.ArchitectureTests/**, tests/README.md, web/e2e/**, web/playwright.config.ts, web/vitest.config.ts, web/package.json, web/package-lock.json, web/tsconfig*.json, web/src/**/*.test.tsx, Directory.Packages.props, Vitrine.slnx, src/Api/Program.cs, src/Api/Vitrine.Api.csproj, src/Api/packages.lock.json, src/AppHost/packages.lock.json]
adrs: [ADR-0002, ADR-0001, ADR-0014]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0003 — Harness de testes e arquitetura

## 1. Visão Geral
Monta a infraestrutura de testes de todos os níveis definidos na ADR-0002 — unitário, integração com PostgreSQL real, contrato, E2E com Playwright e testes de arquitetura com ArchUnitNET — cada um com um exemplo funcionando localmente, e registra os comandos no `sdd-config.yml`. A partir daqui toda spec escreve seus testes sobre essa base.

## 2. Motivação & Escopo
**Motivação:** o veto de testes do processo exige que unitário, integração, E2E e arquitetura existam e rodem antes da primeira funcionalidade.

**Objetivos (dentro do escopo):**
- Projetos `tests/Vitrine.UnitTests`, `tests/Vitrine.IntegrationTests` (Testcontainers.PostgreSql + `WebApplicationFactory` + WireMock.Net), `tests/Vitrine.ArchitectureTests` (ArchUnitNET), `web/e2e/` (Playwright, perfis desktop e celular, axe) e Vitest na SPA.
- Fixture compartilhada de Postgres por coleção de testes, com banco isolado por teste (schema/database descartável).
- Fixture E2E que sobe a aplicação inteira pelo AppHost (`Aspire.Hosting.Testing`) e expõe a URL ao Playwright.
- Regras de arquitetura da ADR-0001 já codificadas (valem para módulos futuros por convenção de nomes): camadas, isolamento entre módulos, sem ciclos, `Common` sem módulos.
- Convenção de tag de teste `SPEC-NNNN:<ID>` (atributo `[Trait("spec", "SPEC-NNNN:UT-01")]` em C#; título do teste em TS) e documentação em `tests/README.md`.
- Comandos `test`, `test_integration`, `test_e2e`, `arch_test` e `coverage` preenchidos no sdd-config e verificados.
- Teste de durabilidade de mensagem Wolverine (ADR-0014) com Postgres real.

**Não-objetivos (fora do escopo):**
- Rodar no CI (SPEC-0004); testes de carga (k6 entra com a spec de respostas); suíte de avaliação de IA (épico de respostas).

## 3. Dependências
- **Implementações necessárias:** SPEC-0002 — solução, AppHost e SPA existem.
- **Contratos consumidos:** N/A
- **Pré-requisitos externos:** Docker local; navegadores do Playwright instalados (`npx playwright install --with-deps chromium`).

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido nos ADRs ADR-0002 (estratégia de testes), ADR-0001 (fronteiras a verificar) e ADR-0014 (durabilidade de mensagens).

**Decisão:** ferramentas por nível exatamente como a tabela da ADR-0002; ArchUnitNET para arquitetura.

**Justificativa:** banco real nos testes de integração e o mesmo AppHost no E2E reduzem a diferença entre teste e produção.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** NetArchTest (sem manutenção desde 2021); banco em memória (não reproduz PostgreSQL).

**ADRs:** ADR-0002, ADR-0001, ADR-0014.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** suíte unitária < 30 s; integração < 5 min; E2E < 5 min no ambiente local — medido pelo tempo do comando registrado no G2.
- **Segurança:** N/A — sem código de produção exposto.
- **Privacidade e dados pessoais:** dados de teste são sintéticos (builders); nenhum dado real em fixtures — revisado no G4.
- **Disponibilidade e resiliência:** testes isolados e determinísticos (sem dependência de ordem ou relógio real) — verificado rodando a suíte 3 vezes em ordem aleatória no G2.
- **Acessibilidade (UI):** helper de axe pronto para as telas da SPEC-0006 — exemplo no E2E-01.
- **Custo:** N/A — execução local.

## 6. Artefato A — Contrato
**Interface:** comandos de teste e convenções consumidos por todas as specs

```text
sdd-config.yml:
  test:             "dotnet test tests/Vitrine.UnitTests && npm --prefix web run test"
  test_integration: "dotnet test tests/Vitrine.IntegrationTests"
  test_e2e:         "npm --prefix web run e2e"            # Playwright; BASE_URL opcional (staging)
  arch_test:        "dotnet test tests/Vitrine.ArchitectureTests"
  coverage:         comando de `coverage` do sdd-config: limpa TestResults/coverage; dotnet test --solution com --coverlet (cobertura);
                    ReportGenerator 5.5.11 via dnx (Cobertura + TextSummary); diff-cover 10.6.0 via uvx contra origin/main com --fail-under=80;
                    sem uvx: aviso local, erro quando CI=true (coletor coverlet.MTP — MIT; ReportGenerator — Apache-2.0; diff-cover — Apache-2.0)
Tag de teste:
  C#: [Trait("spec", "SPEC-NNNN:UT-01")]     TS: test("SPEC-NNNN:E2E-01 — <descrição>", ...)
Fixtures públicas:
  PostgresFixture (IAsyncLifetime) -> connection string de banco isolado por teste
  ApiFactory : WebApplicationFactory<Program> -> HttpClient com cookies; troca de serviços externos por WireMock
  AppHostFixture (E2E) -> sobe AppHost; exporta BASE_URL para o Playwright
Sem BASE_URL o E2E sobe a aplicação pelo AppHost; com BASE_URL roda contra o ambiente informado.
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Teste unitário de exemplo | `dotnet test tests/Vitrine.UnitTests` | passa e é descoberto com a tag de spec | UT-01 |
| Teste unitário da SPA | `npm run test` | Vitest roda e passa | UT-02 |
| Postgres real isolado | dois testes gravam a mesma chave | cada um vê só os próprios dados | IT-01 |
| Mensagem durável | mensagem Wolverine enviada e host reiniciado antes do processamento | mensagem processada após o reinício | IT-02 |
| Serviço externo falso | API configurada para chamar URL externa | chamada atendida pelo WireMock, nenhuma saída à internet | IT-03 |
| Violação de camada | `Domain` de um módulo de amostra referencia `Infrastructure` | teste de arquitetura falha | UT-03 |
| Violação entre módulos | módulo A referencia `Application` do módulo B | teste de arquitetura falha | UT-04 |
| Código conforme | solução atual | todos os testes de arquitetura passam | UT-05 |
| E2E com a aplicação real | Playwright contra o AppHost | página inicial carrega em desktop e celular, sem violações axe sérias | E2E-01 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado o projeto `Vitrine.UnitTests`, quando executado, então um teste de exemplo marcado `SPEC-0003:UT-01` é descoberto e passa (comprova descoberta e convenção de tag).
- **UT-02** — Dado a SPA, quando `npm run test` roda, então um teste Vitest de exemplo do componente raiz passa.
- **UT-03** — Dado um assembly de amostra (fixture de teste) em que `Sample.Domain` referencia `Sample.Infrastructure`, quando a regra de camadas roda, então ela falha apontando a dependência proibida.
- **UT-04** — Dado assemblies de amostra em que `ModuleA.Application` referencia `ModuleB.Application`, quando a regra de isolamento de módulos roda, então ela falha.
- **UT-05** — Dado os assemblies reais da solução, quando todas as regras de arquitetura rodam, então passam (guarda contra regressão estrutural).

### 7.3 Testes de Integração
- **IT-01** — Dado a `PostgresFixture`, quando dois testes paralelos gravam e leem o mesmo registro em bancos isolados, então nenhum vê os dados do outro.
- **IT-02** — Dado Wolverine com persistência no PostgreSQL, quando uma mensagem é publicada e o host é parado antes de processá-la e reiniciado, então o handler processa a mensagem exatamente uma vez.
- **IT-03** — Dado a `ApiFactory` com WireMock substituindo um cliente HTTP externo nomeado, quando a API faz a chamada, então o WireMock registra a requisição e nenhuma conexão sai para a internet.

### 7.4 Testes de Contrato
- N/A — esta spec não fornece nem consome contrato via `consumes_contract`; o mecanismo de teste de contrato de eventos é usado a partir do primeiro evento de integração (épicos de produto).

### 7.5 Testes E2E
- **E2E-01** — Dado a aplicação subida pelo AppHost, quando o Playwright abre `/` nos perfis desktop e celular, então a página carrega o elemento raiz e o axe não reporta violações sérias ou críticas.

### 7.6 Outros
- N/A

**Dublês e dados de teste:** WireMock.Net para HTTP externo; builders de dados sintéticos; assemblies de amostra só para UT-03/UT-04 em `tests/Vitrine.ArchitectureTests/Samples`.

**Ambiente de execução:** local com Docker; no CI a partir da SPEC-0004.

## 8. Plano de Rollout
- **Estratégia:** deploy direto — só código de teste; entrega = merge do PR.
- **Dados/schema:** N/A — schemas de teste criados e descartados pelos próprios testes.
- **Compatibilidade:** N/A
- **Observabilidade:** N/A — sem serviço em execução.
- **Rollback:** revert do merge commit.
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Unitário e arquitetura**
- [x] Red: escrever UT-01..UT-05 com a tag `SPEC-0003:<ID>` e confirmar que falham pelo motivo certo (UT-05 é guarda: passa)
- [x] Green: projetos UnitTests e ArchitectureTests, Vitest na SPA, regras ArchUnitNET da ADR-0001, assemblies de amostra
- [x] Refactor mantendo tudo verde

**Fase 2: Integração com dependências reais**
- [x] Red: escrever IT-01, IT-02, IT-03 e confirmar que falham pelo motivo certo
- [x] Green: `PostgresFixture` (Testcontainers), `ApiFactory` + WireMock, Wolverine com persistência PostgreSQL no host
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase 3: Jornada E2E**
- [x] Red: E2E-01 falhando pelo motivo certo
- [x] Green: Playwright (desktop + celular) + axe contra o AppHost; comandos `test`, `test_integration`, `test_e2e`, `arch_test`, `coverage` no sdd-config

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy: N/A — só código de teste (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe (árvore suja) | 2026-10-13 |
| G1 Red | PASS | verify G1: PASS; `dotnet test --solution Vitrine.slnx` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — e7bf65f | 2026-10-13 |
| G2 Green | PASS | build exit 0 (✓ built in 333ms); test exit 0 (Duration  1.62s (environment 50%, import 29%, transform 11%, tests 9%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (AVISO: uvx não encontrado; diff-cover (>= 80% das linhas alteradas) NÃO foi verificado loc) — 1cb9efc | 2026-10-13 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 140ms) — 1cb9efc | 2026-10-13 |
| G4 Review | PASS | MANUAL: verify SPEC-0003 --base 326c5c2 (integração antes do merge): PASS 0 falhas, 1 aviso justificado; a FAIL anterior apontava .claude/scheduled_tasks.lock, arquivo do agendador commitado por engano pelo Architect em 750c582 e removido em seguida — fora do trabalho da spec | 2026-10-13 |
| G5 Integração & CI | PASS | MANUAL: PR #8 no resultado integrado ca587e5: 7/7 checks obrigatórios verdes (build-lint, unit, integration, architecture, e2e, security, sdd/pr-check) — https://github.com/thomasravache/vitrine/actions/runs/38503495881/job/115656660941; local: build 0 avisos, dotnet 21/21, Vitest 1/1, E2E 2/2, infra 3/3, ci 2/2, lint ok | 2026-10-13 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-13 |
| G6 Deploy | N/A | sem serviço implantável: só harness de testes; deploy na nuvem na SPEC-0009 | 2026-10-14 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 464d0b7 | 2026-10-14 |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-13 | G2 | spec | Comando coverage do contrato (--collect XPlat + reportgenerator) não existe sob Microsoft Testing Platform e nenhum ADR define ferramenta de cobertura de linhas alteradas | MTP sem --collect; coverlet.MTP 10.1.0 + ReportGenerator 5.5.11 funcionou em teste descartável; diff-cover indisponível localmente | Architect + thomas (emenda) | Emenda v2 — coverage com coverlet.MTP + ReportGenerator + diff-cover (≥80% linhas alteradas, obrigatório no CI); ADR-0002 atualizado; aprovada por thomas em 2026-10-13 | 2026-10-13 |
| IMP-02 | 2026-10-13 | G5 | externo | Chromium do Playwright não abre: faltam bibliotecas de sistema (libatk); E2E-01 não executado localmente | Instalação sem sudo não é possível; fixture do AppHost e baseURL validados com teste de request descartável | thomas: rodar 'cd web && sudo npx playwright install-deps chromium' | thomas instalou as dependências do Chromium; npm --prefix web run e2e → E2E-01 2/2 PASS (desktop + mobile, axe sem violações sérias/críticas) em 5da9752, 2026-10-13 | 2026-10-13 |
| IMP-03 | 2026-10-13 | G2 | spec | Comando coverage roda em todos os projetos de teste, mas tests/Vitrine.Tooling.Tests (fora dos touches) não tem coverlet.MTP e rejeita --coverlet | Ajuste temporário no csproj do Tooling.Tests fez o comando passar (100% diff coverage); revertido | Architect (ajuste de escopo) | Ajuste de escopo dentro da Emenda v2 (sem mudança de contrato): touches incluem csproj, lockfile e testconfig do Vitrine.Tooling.Tests | 2026-10-13 |

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
Harness de testes de todos os níveis da ADR-0002: xUnit v3 (unitário), integração com PostgreSQL real (Testcontainers) e serviços externos falsos (WireMock), testes de arquitetura ArchUnitNET com as regras da ADR-0001 (camadas, isolamento de módulos, sem ciclos, Common sem módulos), Vitest + happy-dom + Testing Library na SPA, Playwright (desktop e celular) com axe subindo a aplicação pelo AppHost, cobertura coverlet.MTP + ReportGenerator + diff-cover (≥ 80% das linhas alteradas, obrigatório no CI). Wolverine com persistência durável no PostgreSQL no host da API, criando o schema na partida só em Development/testes (ADR-0011).

### Como foi feito
Test-writer, Implementer e Reviewer independentes. Emenda v2 (cobertura) aprovada por thomas; IMP-01 (comando de cobertura), IMP-02 (bibliotecas do Chromium, instaladas pelo usuário) e IMP-03 (escopo do Tooling.Tests) resolvidos. Review pediu: happy-dom 20.14.6 (ADR-0002 atualizado), Wolverine sem auto-criação em produção, IT-02 sem dependência de relógio, testes negativos para ciclos e Common, carregamento de todos os projetos de src/, testconfig único. Minors aceitos: IT-02 usa APIs internas do Wolverine (rever no upgrade); SolutionArchitecture carrega DLL de bin/ se o projeto não for referenciado.

### Prova de Correção
N/A

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| SPEC-0003:UT-01 | Harness unitário descoberto com tag | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:UT-02 | Vitest roda na SPA | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:UT-03 | Regra de camadas falha na violação | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:UT-04 | Regras de isolamento, ciclos e Common falham nas violações | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:UT-05 | Solução atual respeita as regras | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:IT-01 | Postgres real isolado por teste | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:IT-02 | Mensagem Wolverine sobrevive ao reinício do host | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:IT-03 | Serviço externo atendido pelo WireMock | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0003:E2E-01 | Página inicial carrega sem violações axe (desktop e celular) | PASS | PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881; local 2/2 após IMP-02 |

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

### Deploy
Sem serviço implantável (G6 N/A). Entrega = merge do PR #8 na `main` (5617ad3).

### Pendências
Nenhuma nova spec. Comandos oficiais no `sdd-config` (test, test_integration, test_e2e, arch_test, coverage).

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0003`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v2 | 2026-10-13 | Comando `coverage` passa a usar coverlet.MTP + ReportGenerator + diff-cover (≥ 80% nas linhas alteradas, obrigatório no CI); `web/biome.json` entra nos touches para ignorar `packages.lock.json`; csproj/lockfile/testconfig do `Vitrine.Tooling.Tests` entram nos touches para aceitar `--coverlet` (ajuste de escopo, IMP-03) | IMP-01: `--collect` não existe no Microsoft Testing Platform e não havia ferramenta de linhas alteradas definida | SPEC-0004 (roda o diff-cover no CI) | thomas (2026-10-13) |

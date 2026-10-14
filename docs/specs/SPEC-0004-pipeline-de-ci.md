---
id: SPEC-0004
title: Pipeline de CI
tier: full
type: foundation
user_facing: false
status: in-progress
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0002]
consumes_contract: []
contract_version: 1
touches: [.github/workflows/ci.yml, .github/dependabot.yml, .github/codeql/**, tests/ci/**, Directory.Packages.props, src/Api/Vitrine.Api.csproj, src/Api/packages.lock.json, src/AppHost/Vitrine.AppHost.csproj, src/AppHost/AppHost.cs, src/AppHost/packages.lock.json]
adrs: [ADR-0004, ADR-0005, ADR-0008, ADR-0003, ADR-0006]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0004 — Pipeline de CI

## 1. Visão Geral
Pipeline no GitHub Actions que roda em todo PR e em todo push na `main`: build, lint, testes unitários, integração, arquitetura e E2E, varreduras de segurança (dependências, segredos, imagem, SAST), o job SDD (`validate` + `pr-check`) e, na `main`, publica a imagem `vitrine-api` no GitHub Container Registry. Os checks viram obrigatórios na proteção da `main`.

## 2. Motivação & Escopo
**Motivação:** a `main` precisa estar sempre verde e publicável; o CI é o portão final que vale para pessoas e agentes.

**Objetivos (dentro do escopo):**
- `.github/workflows/ci.yml` com jobs `build-lint`, `unit`, `integration`, `architecture`, `e2e`, `security` e `sdd` (gerado por `vendor --ci github` e ajustado).
- Cache de NuGet e npm; artefatos de relatório (cobertura, relatório Playwright, traces) anexados à execução.
- `security`: `dotnet list package --vulnerable --include-transitive`, `npm audit --audit-level=high`, gitleaks, Trivy na imagem, CodeQL (C# e TS).
- `AddDockerComposeEnvironment` no AppHost (Aspire.Hosting.Docker) para o `aspire publish` gerar o `compose-bundle`.
- Na `main`: build da imagem única (API + SPA estática) via `dotnet publish` com container ou Dockerfile gerado pelo `aspire publish`, publicada como `ghcr.io/thomasravache/vitrine-api:<sha>`.
- `.github/dependabot.yml` (nuget, npm, docker, github-actions; semanal, agrupado).
- Checks obrigatórios configurados na proteção da `main`.
- Comandos `security_scan` e `ci` no sdd-config.

**Não-objetivos (fora do escopo):**
- Deploy em ambientes (SPEC-0005).

## 3. Dependências
- **Implementações necessárias:** SPEC-0002 — repositório no GitHub, comandos `build` e `lint`.
- **Contratos consumidos:** N/A — os comandos de teste da SPEC-0003 são chamados pelo nome do sdd-config; enquanto a SPEC-0003 não estiver integrada, os jobs de teste rodam o que existir (a wave seguinte valida tudo junto no G5).
- **Pré-requisitos externos:** GitHub Actions habilitado no repositório; permissão `packages: write` para o GHCR; CodeQL disponível para o repositório (gratuito em repositório público; em privado requer GitHub Code Security — ver Questões em Aberto do épico sobre visibilidade).

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0004 (entrega), ADR-0005 (fluxo de mudança), ADR-0008 (dependências), ADR-0003 (qualidade) e ADR-0006 (segredos).

**Decisão:** um workflow de CI com jobs paralelos por nível, imagem imutável por SHA publicada só a partir da `main`.

**Justificativa:** feedback paralelo e rápido; a imagem testada é exatamente a que vai para staging e produção.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** um job sequencial único (lento, falha opaca); build da imagem em cada ambiente (deixaria de ser a mesma imagem testada).

**ADRs:** ADR-0004, ADR-0005, ADR-0008, ADR-0003, ADR-0006.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** PR completo em ≤ 15 min (p50) — medido pela duração das execuções registrada no G5.
- **Segurança:** permissões mínimas por job (`permissions:` explícito, `contents: read` por padrão); actions de terceiros fixadas por SHA; segredo nenhum exposto a PRs de forks — revisado no G4.
- **Privacidade e dados pessoais:** N/A — sem dados pessoais no CI.
- **Disponibilidade e resiliência:** reexecução manual permitida; sem retry automático de testes (teste instável é defeito, ADR-0002).
- **Acessibilidade (UI):** N/A — sem interface; o axe roda dentro do job `e2e`.
- **Custo:** minutos de Actions: repositório público é gratuito; privado no plano Free tem cota mensal limitada — consumo acompanhado no G5 e reavaliado se passar de 80% da cota.

## 6. Artefato A — Contrato
**Interface:** `.github/workflows/ci.yml` — checks e artefatos (consumido pela SPEC-0005)

```text
Gatilhos: pull_request (main), push (main), workflow_dispatch
Checks obrigatórios (nomes estáveis): build-lint, unit, integration, architecture, e2e, security, sdd
Saída na main (após todos os checks): imagem ghcr.io/thomasravache/vitrine-api:<git-sha> (+ tag "main")
                                      artefato "compose-bundle" (saída do `aspire publish`: docker-compose.yaml + .env.template)
Falhas: qualquer job vermelho bloqueia o merge; job `security` falha em vulnerabilidade alta/crítica ou segredo encontrado
Evento para deploy: workflow_run "CI" concluído com sucesso na main -> consumido por deploy.yml (SPEC-0005)
sdd-config: security_scan: "dotnet list package --vulnerable --include-transitive && npm --prefix web audit --audit-level=high && gitleaks detect --no-banner"
            ci: "gh pr checks --watch"
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| PR saudável | PR com código e testes passando | todos os checks obrigatórios verdes | IT-01 |
| Teste quebrado | PR com teste falhando | job correspondente vermelho; merge bloqueado | IT-02 |
| Segredo commitado | PR com chave falsa no formato de token | job `security` falha (gitleaks) | IT-03 |
| Dependência vulnerável | pacote com CVE alta | job `security` falha | IT-04 |
| Push na main | merge na `main` com checks verdes | imagem `vitrine-api:<sha>` e `compose-bundle` publicados | IT-05 |
| Spec sem gates | PR que cita spec sem G0–G4 | job `sdd` (pr-check) falha | CT-01 |
| Contrato do evento de deploy | execução de CI concluída na main | `deploy.yml` encontra imagem e bundle pelo SHA | CT-02 |

## 7. Artefato B — Plano de Testes (TDD)
<!-- Testes de pipeline rodam como PRs/branches de prova no próprio repositório; a evidência é o link da execução. -->

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado `ci.yml`, quando validado com `actionlint`, então não há erros e todos os `uses:` de terceiros estão fixados por SHA (script de verificação em `tests/ci/`).
- **UT-02** — Dado `ci.yml`, quando lido pelo script de verificação, então os nomes dos jobs são exatamente os checks obrigatórios do contrato e todo job declara `permissions`.

### 7.3 Testes de Integração
- **IT-01** — Dado um PR de prova com mudança trivial, quando o CI roda, então os 7 checks obrigatórios terminam verdes (link da execução como evidência).
- **IT-02** — Dado um PR de prova com um teste unitário quebrado de propósito, quando o CI roda, então `unit` fica vermelho e o GitHub bloqueia o merge.
- **IT-03** — Dado um PR de prova com um token falso em arquivo, quando o CI roda, então `security` falha com achado do gitleaks.
- **IT-04** — Dado um PR de prova que adiciona uma versão de pacote com vulnerabilidade alta conhecida, quando o CI roda, então `security` falha.
- **IT-05** — Dado um merge na `main`, quando o CI conclui, então a imagem `ghcr.io/thomasravache/vitrine-api:<sha>` existe e o artefato `compose-bundle` está anexado à execução.

### 7.4 Testes de Contrato
- **CT-01** — Dado um PR de prova que cita uma spec sem gates registrados, quando o job `sdd` roda `pr-check`, então falha com a anotação correspondente.
- **CT-02** — Dado uma execução de CI concluída na `main`, quando um workflow de prova consome `workflow_run` e busca a imagem e o artefato pelo `head_sha`, então ambos são encontrados (contrato com a SPEC-0005).

### 7.5 Testes E2E
- N/A — `user_facing: false`: pipeline sem jornada de usuário; o job `e2e` executa os E2E das outras specs.

### 7.6 Outros
- N/A

**Dublês e dados de teste:** PRs e branches de prova `ci-proof/*`, fechados após registrar a evidência; token falso no formato reconhecido pelo gitleaks (nunca um token real).

**Ambiente de execução:** GitHub Actions (runners `ubuntu-latest`).

## 8. Plano de Rollout
- **Estratégia:** deploy direto — workflow ativo a partir do merge.
- **Dados/schema:** N/A
- **Compatibilidade:** N/A
- **Observabilidade:** histórico de execuções do Actions (base das métricas DORA, ADR-0004); falha na `main` notifica o responsável por e-mail do GitHub.
- **Rollback:** revert do merge commit; remoção dos checks obrigatórios só por decisão humana.
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Workflow**
- [ ] Red: escrever UT-01, UT-02 (`tests/ci/`) com a tag `SPEC-0004:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `ci.yml` com os 7 jobs, permissões mínimas, actions fixadas por SHA, cache, `dependabot.yml`, CodeQL, `AddDockerComposeEnvironment` no AppHost, imagem no GHCR
- [ ] Refactor mantendo tudo verde

**Fase 2: Provas no GitHub**
- [ ] Red→Green: IT-01..IT-05 e CT-01, CT-02 com PRs/branches de prova `ci-proof/*` (link das execuções)
- [ ] Checks obrigatórios na proteção da `main`; `security_scan` e `ci` no sdd-config
- [ ] Validar: build + suíte completa (G2)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy: N/A — workflow ativo no merge (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe (árvore suja) | 2026-10-13 |
| G1 Red | PASS | MANUAL: verify G1 PASS (9/9 tags, a4cbee3); testes são scripts em tests/ci (fora do comando test): run.sh → UT-01/UT-02 FAIL 'ci.yml does not exist'; IT-01..05, CT-01, CT-02 falham por ci.yml ausente/variáveis de prova ausentes — a4cbee3 | 2026-10-13 |
| G2 Green | PENDING | | |
| G3 Arquitetura | PENDING | | |
| G4 Review | PENDING | | |
| G5 Integração & CI | PENDING | | |
| H2 Integração aprovada | PENDING | | |
| G6 Deploy | PENDING | | |
| G7 Pronto & Docs | PENDING | | |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
<!-- comportamento entregue do ponto de vista do usuário/sistema -->

### Como foi feito
<!-- decisões de implementação, módulos/arquivos principais, desvios e emendas (com versão), dívidas assumidas -->

### Prova de Correção
<!-- type fix: o teste de regressão falhou antes da correção (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->

### Verificação
<!-- Uma linha por teste do plano (todos os IDs da seção 7). Resultado: PASS. Evidência: execução de CI, commit ou relatório. -->
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|

### Definição de Pronto
- [ ] Todos os testes do plano passando e listados na Verificação
- [ ] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [ ] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [ ] Review independente sem achados blocker/major (G4)
- [ ] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [ ] Requisitos não-funcionais medidos com evidência (ou N/A justificado)
- [ ] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6)
- [ ] Observabilidade e rollback prontos conforme o Plano de Rollout
- [ ] Documentação raiz e CHANGELOG atualizados (G7)
- [ ] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0004`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

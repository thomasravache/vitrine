---
id: SPEC-0007
title: Observabilidade de base
tier: full
type: foundation
user_facing: false
status: in-progress
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0006]
consumes_contract: [SPEC-0006@1]
contract_version: 1
touches: [src/ServiceDefaults/**, src/Api/Observability/**, src/Api/Program.cs, src/Api/appsettings*.json, src/Api/Vitrine.Api.csproj, src/Modules/Identity/Vitrine.Identity.Infrastructure/**, src/Modules/Identity/Vitrine.Identity.Presentation/**, web/src/observability/**, web/src/main.tsx, web/src/vite-env.d.ts, web/package.json, web/package-lock.json, deploy/alloy/**, deploy/compose.yaml, deploy/compose.override.yaml, deploy/local/.env.example, docs/runbooks/**, tests/Vitrine.IntegrationTests/Observability/**, tests/Vitrine.IntegrationTests/Support/**, tests/Vitrine.UnitTests/Observability/**, tests/Vitrine.UnitTests/Vitrine.UnitTests.csproj, tests/Vitrine.IntegrationTests/Vitrine.IntegrationTests.csproj, Directory.Packages.props, "**/packages.lock.json"]
adrs: [ADR-0009, ADR-0006, ADR-0004]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0007 — Observabilidade de base

## 1. Visão Geral
Liga a telemetria que o Aspire já produz (OpenTelemetry via ServiceDefaults) a destinos de produção: logs, métricas e traces para o Grafana Cloud, erros do backend e da SPA para o Sentry; expõe health checks de liveness e readiness, métricas da jornada de acesso, um alerta sobre essa jornada e alertas de saturação do servidor, com runbooks.

## 2. Motivação & Escopo
**Motivação:** sem sinais e alertas, o primeiro aviso de problema seria o vendedor; os épicos seguintes (respostas em 2 minutos) dependem dessa base para medir SLO.

**Objetivos (dentro do escopo):**
- `/health/live` (processo) e `/health/ready` (inclui PostgreSQL) em todos os ambientes, anônimos e sem detalhes sensíveis.
- Exportação OTLP para Grafana Cloud (endpoint e credenciais por secret do Environment), com `service.name`, `deployment.environment` e versão (SHA).
- Logs estruturados JSON com `trace_id`; enriquecedor que mascara e-mail, senha, tokens e cabeçalhos `Authorization`/`Cookie`.
- Sentry no backend (`Sentry.AspNetCore`) e na SPA, com release = SHA e PII desligada.
- Métricas da jornada de acesso: `vitrine.auth.signups`, `vitrine.auth.logins{result}`, `vitrine.auth.lockouts`.
- Coleta de métricas do host (CPU, memória, disco) por Grafana Alloy em container no Compose.
- Regras de alerta versionadas no repositório (verificação sintética de `/health/ready` e de `/entrar` a cada 1 min, falha 3x; disco > 80%; memória > 90%; 5xx > 5% em 5 min; destino e-mail do responsável), aplicadas no Grafana Cloud pela SPEC-0009.
- Exportação para Grafana Cloud e Sentry **opcional por configuração**: sem as variáveis, a telemetria vai só para o dashboard do Aspire (ambiente local).
- SLO inicial: disponibilidade ≥ 99,5% mensal da jornada `acesso` (painel de SLO no Grafana).
- Runbooks em `docs/runbooks/` para cada alerta.

**Não-objetivos (fora do escopo):**
- SLO e métricas de respostas automáticas (épico de respostas); dashboards de negócio.

## 3. Dependências
- **Implementações necessárias:** SPEC-0006 — os testes de integração exercitam os endpoints de autenticação reais.
- **Contratos consumidos:** SPEC-0006@1 — endpoints de auth (para métricas e para a verificação sintética de `/entrar`) e a jornada `acesso`.
- **Pré-requisitos externos:** N/A nesta versão — contas Grafana Cloud e Sentry ficam para a SPEC-0009.

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0009 (OpenTelemetry → Grafana Cloud + Sentry), ADR-0006 (nada sensível em telemetria), ADR-0004 (Compose no droplet).

**Decisão:** manter a instrumentação padrão do Aspire ServiceDefaults e configurar só exportadores e enriquecedores; Alloy como único agente no host.

**Justificativa:** sem lock-in de instrumentação; o mesmo código mostra telemetria no dashboard do Aspire localmente e no Grafana em produção.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** stack de observabilidade auto-hospedada (consome o droplet); Application Insights (fora do ecossistema escolhido).

**ADRs:** ADR-0009, ADR-0006, ADR-0004.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** overhead de telemetria < 5% de CPU no droplet de produção — medido no Grafana durante o G6; volume dentro do plano gratuito (≤ 10 mil séries, ≤ 50 GB/sinal).
- **Segurança:** endpoints de health sem dados internos (só status); credenciais OTLP/Sentry só em secrets — IT-01, revisão G4.
- **Privacidade e dados pessoais:** e-mail, senha, tokens e cookies nunca em logs/traces/eventos do Sentry — IT-03; `SendDefaultPii=false`.
- **Disponibilidade e resiliência:** falha do destino de telemetria não afeta a aplicação (exportação assíncrona com buffer limitado) — IT-04.
- **Acessibilidade (UI):** N/A — sem tela nova.
- **Custo:** US$ 0 no plano gratuito (ADR-0009/0012); alerta se o uso passar de 80% da cota.

## 6. Artefato A — Contrato
**Interface:** `GET /health/live` · `GET /health/ready` · métricas `vitrine.auth.*` · variáveis de configuração de telemetria

```text
GET /health/live  -> 200 "Healthy" (processo vivo)              | 503 "Unhealthy"
GET /health/ready -> 200 {"status":"Healthy"} (banco acessível)   | 503 {"status":"Unhealthy"}
  anônimos; nunca expõem nomes de host, connection strings ou exceções
Métricas (OTel, meter "Vitrine.Identity"):
  vitrine.auth.signups (counter) · vitrine.auth.logins (counter, tag result=success|failure|locked) · vitrine.auth.lockouts (counter)
Configuração (secrets por Environment):
  OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_HEADERS, OTEL_RESOURCE_ATTRIBUTES=deployment.environment=<env>,service.version=<sha>
  Sentry__Dsn, VITE_SENTRY_DSN (SPA, build-time)
Alertas (Grafana): synthetic-ready, synthetic-login-page, disk-80, memory-90, http-5xx-rate -> e-mail do responsável
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Processo vivo | GET /health/live | 200 | IT-01 |
| Pronto com banco | GET /health/ready, banco ok | 200 Healthy | IT-01 |
| Banco fora | GET /health/ready, banco parado | 503 Unhealthy sem detalhes internos | IT-02 |
| Mascaramento | log com e-mail, senha, token, cookie | valores mascarados no JSON emitido | UT-01, IT-03 |
| Mascaramento no Sentry | evento/breadcrumb com e-mail, cabeçalhos, cookies, query e corpo | mascarados ou removidos antes do envio; sem DSN, Sentry desligado | UT-02 |
| Destino de telemetria fora | endpoint OTLP inacessível | requisições seguem normais | IT-04 |
| Métricas de login | logins ok/falha/bloqueio | contadores com a tag `result` correta | IT-05 |
| Contrato de auth | login de prova via API | métricas e trace com spans de HTTP e banco | CT-01 |
| Exportação opcional | variáveis OTLP/Sentry ausentes | aplicação sobe e exporta só para o dashboard do Aspire | IT-06 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado o enriquecedor de mascaramento, quando recebe propriedades com e-mail, `password`, `access_token`, `Authorization` e `Cookie`, então emite os valores mascarados (`***`) e mantém os demais campos.
- **UT-02** — Dado a configuração do Sentry do backend com DSN definido, quando um evento traz e-mail na mensagem e na exceção, cabeçalhos `Authorization`/`Cookie`, cookies, query string com token e corpo da requisição, e um breadcrumb com e-mail, então o evento enviado tem esses valores mascarados ou removidos, `SendDefaultPii=false`, e release/ambiente vêm da configuração; sem DSN, o Sentry não é registrado.

### 7.3 Testes de Integração
- **IT-01** — Dado a API com Postgres real, quando `GET /health/live` e `GET /health/ready`, então ambos 200 e o corpo não contém host, connection string nem stack trace.
- **IT-02** — Dado o container de Postgres parado, quando `GET /health/ready`, então 503 sem detalhes internos, e `/health/live` continua 200.
- **IT-03** — Dado o provedor de logs capturado em memória, quando um login com e-mail e senha falha, então nenhum registro emitido contém o e-mail nem a senha.
- **IT-04** — Dado `OTEL_EXPORTER_OTLP_ENDPOINT` apontando para porta fechada, quando 50 requisições são feitas, então todas respondem com sucesso e latência p95 dentro de +10% da linha de base.
- **IT-05** — Dado um `MeterListener` de teste, quando ocorrem um login bem-sucedido, um falho e um bloqueio, então `vitrine.auth.logins` registra `success`, `failure` e `locked` e `vitrine.auth.lockouts` incrementa.
- **IT-06** — Dado o host sem `OTEL_EXPORTER_OTLP_ENDPOINT` nem `Sentry__Dsn`, quando a API sobe e atende requisições, então nenhum erro de configuração ocorre e o exportador do Aspire (`DOTNET_DASHBOARD_OTLP_ENDPOINT_URL`) recebe os traces.
- Alerta em produção e telemetria em staging: provados pelos testes de integração da SPEC-0009.

### 7.4 Testes de Contrato
- **CT-01** — Dado o contrato SPEC-0006@1, quando um login de prova é feito via `POST /api/v1/auth/login`, então o trace exportado (exportador em memória) contém span HTTP da rota e span de banco filhos do mesmo `trace_id`, e a métrica `vitrine.auth.logins{result=success}` incrementa.

### 7.5 Testes E2E
- N/A — `user_facing: false`: sem mudança de jornada; a jornada `acesso` continua coberta pelo E2E de acesso da SPEC-0006, usado aqui como verificação sintética.

### 7.6 Outros
- N/A

**Dublês e dados de teste:** exportadores OTel em memória; `MeterListener`; logger capturador; Postgres real (Testcontainers).

**Ambiente de execução:** local + CI.

## 8. Plano de Rollout
- **Estratégia:** deploy direto — só telemetria e endpoints de health.
- **Dados/schema:** N/A
- **Compatibilidade:** N/A — rotas novas.
- **Observabilidade:** esta spec cria a base: painéis de requisições, SLO da jornada `acesso`, alertas listados no contrato.
- **Rollback:** redeploy da tag anterior; alertas podem ser silenciados no Grafana pelo responsável. Limite que dispara rollback após deploy: taxa de 5xx > 5% em 5 min ou `/health/ready` falhando 3 verificações seguidas.
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Mascaramento e health**
- [ ] Red: escrever UT-01, IT-01, IT-02, IT-03 com a tag `SPEC-0007:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: enriquecedor de mascaramento, `/health/ready` com checagem do banco
- [ ] Refactor mantendo tudo verde

**Fase 2: Telemetria e métricas**
- [ ] Red: escrever IT-04, IT-05, CT-01 e confirmar que falham pelo motivo certo
- [ ] Green: exportação OTLP com atributos de ambiente/versão, métricas `vitrine.auth.*`, Sentry backend e SPA, Alloy no Compose
- [ ] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase 3: Exportação opcional e alertas como código**
- [ ] Red→Green: IT-06; regras de alerta e runbooks versionados (aplicação no Grafana na SPEC-0009)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like (SPEC-0005) com smoke; nuvem na SPEC-0009 (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe (árvore suja) | 2026-10-13 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 889eea8 | 2026-10-28 |
| G2 Green | FAIL | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  340ms (environment 60%, import 24%, transform 9%, tests 7%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 1 (Failure. Coverage is below 80%.) — 387784c | 2026-10-28 |
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0007`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.3 (plano de testes, sem mudança de contrato) | 2026-10-28 | Inclui UT-02: mascaramento do Sentry do backend (`BeforeSend`/`BeforeBreadcrumb`) e registro só com DSN | G2: cobertura das linhas alteradas 79% (< 80%), com a configuração do Sentry em `ObservabilityExtensions.cs` sem teste — proteção de dados pessoais (ADR-0006) precisa de prova | — | thomas (2026-10-28, piloto automático) |
| v1.2 (escopo, sem mudança de contrato) | 2026-10-28 | `touches` ganha os csproj de `Vitrine.UnitTests` e `Vitrine.IntegrationTests` (pacote InMemory e referência ao ServiceDefaults); seção 7 deixa de citar IDs de testes de outras specs | `verify` do G1: arquivos fora do escopo e IDs de outras specs lidos como do plano | — | thomas (2026-10-28, piloto automático) |
| v1.1 (escopo, sem mudança de contrato) | 2026-10-28 | `touches` alinhado ao código real: projetos `Vitrine.Identity.Infrastructure`/`Presentation` (o login fica em `IdentityEndpoints` e `CookieSessions`), suporte de testes de integração, `appsettings`, csproj da API, `web/package.json`/`main.tsx` (Sentry na SPA), `deploy/compose.yaml` e `.env.example` (variáveis opcionais), lockfiles. Versões: Sentry.AspNetCore 6.12.1, @sentry/react 11.7.0, grafana/alloy v1.20.1, OpenTelemetry.Exporter.InMemory 1.19.1 (testes), Npgsql.OpenTelemetry 10.0.3 — registradas no ADR-0009 | A spec foi escrita antes do módulo Identity existir; o caminho `src/Modules/Identity/Infrastructure/Metrics/**` não corresponde à estrutura criada na SPEC-0006 | — | thomas (2026-10-28, piloto automático) |
| v1 (sem mudança de contrato) | 2026-10-13 | IT-06/IT-07 de produção/staging movidos para a SPEC-0009; novo IT-06 (exportação opcional); alertas como código, aplicados na SPEC-0009 | Usuário: nada que gere custo até tudo rodar localmente | SPEC-0009 | thomas (2026-10-13) |

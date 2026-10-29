---
id: SPEC-0009
title: Provisionamento na nuvem e go-live
tier: full
type: foundation
user_facing: true
status: approved
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0005, SPEC-0006, SPEC-0007, SPEC-0008]
consumes_contract: []
contract_version: 1
touches: [docs/runbooks/**, tests/infra/**, infra/environments/**, .github/workflows/deploy.yml]
adrs: [ADR-0004, ADR-0006, ADR-0009, ADR-0010, ADR-0012]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0009 — Provisionamento na nuvem e go-live

## 1. Visão Geral
Última etapa da fundação: depois que tudo roda localmente e o usuário testou, provisiona os ambientes pagos (DigitalOcean, domínio, PostgreSQL gerenciado, Spaces), liga o `deploy.yml`, conecta telemetria de produção (Grafana Cloud, Sentry) e coloca o skeleton em staging e produção pelo pipeline, com restauração de backup e alertas ensaiados.

## 2. Motivação & Escopo
**Motivação:** decisão do usuário (2026-10-13) — nada que gere custo até o produto estar "tinindo" localmente e testado por ele. Esta spec concentra tudo o que depende de conta paga ou de ambiente na nuvem.

**Objetivos (dentro do escopo):**
- Pré-requisitos do usuário: domínio registrado, conta DigitalOcean com token, contas Grafana Cloud e Sentry, secrets nos GitHub Environments `staging` e `production` (este com aprovação obrigatória).
- `tofu apply` de staging e produção a partir de `infra/` (SPEC-0005); preços reconfirmados no painel e registrados no ADR-0012; alerta de gasto US$ 100.
- Ligar `DEPLOY_ENABLED`; staging automático a cada merge; produção por `workflow_dispatch` com aprovação.
- Telemetria de produção: exportação OTLP para Grafana Cloud, Sentry, alertas (sintético, saturação, 5xx) com destino e-mail (SPEC-0007 entrega o código; aqui só configuração e prova).
- Restauração PITR ensaiada; usuário de prova em produção; smoke/E2E `@smoke` em staging e produção.
- `deploy_staging`, `deploy_production`, `smoke_test`, `staging_url`, `production_url` do sdd-config apontando para a nuvem.
- Pendências herdadas da SPEC-0005: PostgreSQL gerenciado na mesma versão maior usada localmente (18, se a DigitalOcean oferecer); deploy passa a usar o `compose-bundle` gerado pelo Aspire (`DEPLOY_COMPOSE_BASE`) em vez do Compose escrito à mão, revisando o serviço de dashboard que o Aspire inclui; `GRANT CREATE` (ou ownership) do banco `vitrine` para `vitrine_app` antes do primeiro `--migrate`; bucket de estado do OpenTofu, domínio real e usuário de deploy revistos.

**Não-objetivos (fora do escopo):**
- Abrir o cadastro público (`Features:SignupOpen` segue desligado em produção até o lançamento).
- Alta disponibilidade.

## 3. Dependências
- **Implementações necessárias:** SPEC-0005 (IaC e scripts de deploy), SPEC-0006 (skeleton), SPEC-0007 (telemetria e alertas em código), SPEC-0008 (segurança de base) — todas implementadas e testadas localmente pelo usuário.
- **Contratos consumidos:** N/A
- **Pré-requisitos externos:** domínio, conta DigitalOcean + token, Grafana Cloud, Sentry e confirmação do usuário de que testou localmente (Questões em Aberto).

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0004 (DigitalOcean + Compose), ADR-0006 (segredos por Environment), ADR-0009 (observabilidade), ADR-0010 (RPO/RTO), ADR-0012 (custo).

**Decisão:** aplicar exatamente o que a SPEC-0005 validou (mesmo `infra/`, mesmo `deploy/deploy.sh`), trocando só o alvo local pela VPS.

**Justificativa:** o go-live não introduz código novo de deploy — reduz o risco a configuração e credenciais.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** provisionar no início da fundação (descartado pelo usuário por custo).

**ADRs:** ADR-0004, ADR-0006, ADR-0009, ADR-0010, ADR-0012.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** merge → staging em ≤ 15 min — medido no IT-02.
- **Segurança:** firewall só 80/443 e 22 restrito; SSH por chave; secrets só nos Environments; cabeçalhos (SPEC-0008) verificados em staging — IT-01, IT-03.
- **Privacidade e dados pessoais:** staging nunca recebe dados de produção; transferência internacional coberta nos termos (ADR-0006) antes de abrir cadastro — revisão G4.
- **Disponibilidade e resiliência:** RPO ≤ 15 min, RTO ≤ 4 h — IT-04 mede o RTO.
- **Acessibilidade (UI):** N/A — sem tela nova.
- **Custo:** ~US$ 56/mês (ADR-0012), preços reconfirmados no provisionamento; alerta em US$ 100.

## 6. Artefato A — Contrato
**Interface:** ambientes `staging`/`production`, `deploy.yml` ligado e URLs públicas

```text
GitHub: vars.DEPLOY_ENABLED=true; Environments staging (sem aprovação) e production (revisor: thomas)
Secrets por Environment: DIGITALOCEAN_TOKEN (repo), DEPLOY_SSH_KEY, DEPLOY_HOST, APP_ENV_FILE (conteúdo do .env),
  OTEL_EXPORTER_OTLP_ENDPOINT/HEADERS, Sentry__Dsn, DataProtection__MasterKey, PROD_PROOF_USER/PASSWORD
URLs: https://staging.<domínio> e https://<domínio> (certificado Let's Encrypt via Caddy)
Fluxo: merge na main -> CI -> deploy.yml (staging) -> smoke ; gh workflow run deploy.yml -f environment=production -f tag=<sha> -> aprovação -> smoke
sdd-config: deploy_staging "merge na main -> deploy.yml" ; deploy_production "gh workflow run deploy.yml -f environment=production -f tag=<sha>" ;
  smoke_test "curl -fsS $BASE_URL/health/live && BASE_URL=$BASE_URL npm --prefix web run e2e -- --grep @smoke"
```

**Design:** N/A — sem interface nova.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Servidor provisionado | `tofu apply` staging | droplet com Docker, SSH só por chave, só 22/80/443 | IT-01 |
| Merge na main | CI verde | staging atualizado automaticamente e smoke verde | IT-02 |
| HTTPS público | acesso por http:// | redireciona para https:// com certificado válido; cabeçalhos de segurança presentes | IT-03 |
| Restauração de backup | PITR de produção para cluster novo | dados restaurados; tempo ≤ RTO | IT-04 |
| Produção com aprovação | dispatch para production | aguarda revisor; após aprovar, implantado e smoke verde | IT-05 |
| Alerta de produção | `/health/ready` falha 3x | alerta chega ao e-mail do responsável | IT-06 |
| Telemetria em staging | requisição e erro de prova | trace no Grafana com env e SHA; erro no Sentry com a release | IT-07 |
| Jornada em produção | usuário de prova | entra e vê o painel em produção (celular) | E2E-01 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado o `sdd-config.yml` e o `deploy.yml` desta spec, quando o script de verificação roda, então URLs, comandos de deploy e `DEPLOY_ENABLED` estão definidos e coerentes entre si.

### 7.3 Testes de Integração
- **IT-01** — Dado staging recriado com `tofu apply`, quando o script de verificação conecta, então Docker ativo, login por senha desabilitado, apenas 22/80/443 respondem e `unattended-upgrades` habilitado (reaproveita `tests/infra` da SPEC-0005 apontando para a nuvem).
- **IT-02** — Dado um merge na `main`, quando o CI termina verde, então o `deploy.yml` implanta a mesma tag em staging e o smoke passa (link da execução).
- **IT-03** — Dado o domínio de staging, quando acessado por `http://`, então redireciona para `https://` com certificado válido e os cabeçalhos da SPEC-0008 estão presentes.
- **IT-04** — Dado o PostgreSQL de produção com dados de prova, quando a restauração PITR para um cluster novo é executada pelo runbook, então os dados estão presentes e o tempo até a aplicação apontar para o cluster novo fica registrado (≤ 4 h).
- **IT-05** — Dado um `workflow_dispatch` para `production`, quando disparado, então fica pendente até aprovação; aprovado, implanta e o smoke passa.
- **IT-06** — Dado produção (antes de clientes), quando o health check é forçado a falhar por janela controlada, então o alerta `synthetic-ready` chega ao e-mail do responsável.
- **IT-07** — Dado staging, quando uma requisição e um erro de prova são gerados, então o trace aparece no Grafana com `deployment.environment=staging` e `service.version=<sha>` e o erro aparece no Sentry com a release correta.

### 7.4 Testes de Contrato
- N/A — esta spec não fornece nem consome contrato via `consumes_contract`; reaproveita a implementação das specs anteriores.

### 7.5 Testes E2E
- **E2E-01** — Dado produção com o usuário de prova, quando o Playwright (perfil celular) entra em `/entrar` e chega ao painel, então vê o nome e o cartão "Conectar conta do Mercado Livre (em breve)" [jornada: acesso] @smoke.

### 7.6 Outros
- Varredura de cabeçalhos em staging com nota A ou superior (Mozilla Observatory) — evidência no G6.

**Dublês e dados de teste:** usuário de prova de produção (senha em secret); dados de prova no IT-04; imagens de prova da SPEC-0005.

**Ambiente de execução:** staging e produção reais na DigitalOcean.

## 8. Plano de Rollout
- **Estratégia:** deploy direto em ambientes novos, sem usuários; cadastro público desligado (`Features:SignupOpen=false`).
- **Dados/schema:** migrações das specs anteriores aplicadas pelo pipeline no primeiro deploy.
- **Compatibilidade:** N/A — primeira versão pública.
- **Observabilidade:** alertas e painéis da SPEC-0007 ligados; limite de rollback: 5xx > 5% em 5 min ou `/health/ready` falhando 3 verificações.
- **Rollback:** `gh workflow run deploy.yml -f environment=<env> -f tag=<anterior>`; infraestrutura: `tofu destroy` do ambiente (sem dados de cliente).
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
- [x] Quando provisionar? — Só depois de tudo rodar localmente e o usuário testar; esta spec é a última da fundação (thomas, 2026-10-13)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 85d1191 | 2026-10-13 |
| G1 Red | PENDING | | |
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0009`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (escopo, proposta) | 2026-10-29 | Herdados da SPEC-0007: secrets `OTEL_EXPORTER_OTLP_*`, `SENTRY_DSN`, `GRAFANA_CLOUD_*`, `DEPLOY_ENVIRONMENT`, `ALERT_EMAIL`; `VITE_SENTRY_DSN`/`VITE_SENTRY_RELEASE` no build da imagem (`.github/workflows/ci.yml`); decidir o ambiente do Sentry da SPA com a mesma imagem em staging e produção; aplicar `deploy/alloy/grafana/*` no Grafana Cloud; medir overhead de CPU (< 5%) e uso da cota | G7 da SPEC-0007 | SPEC-0007 | (pendente — revisar juntos no go-live) |
| v1.2 (escopo, proposta) | 2026-10-29 | Herdados da SPEC-0008: secret `DATA_PROTECTION_MASTER_KEY` (32 bytes, `openssl rand -base64 32`) no `DEPLOY_ENV_FILE` de staging e produção, com cópia no cofre pessoal (runbook `docs/runbooks/rotacao-chave-mestra.md`); `PROXY_SUBNET`, `RATE_LIMIT_*` revisados por ambiente; varredura de cabeçalhos (Mozilla Observatory) em staging | G7 da SPEC-0008 | SPEC-0008 | (pendente — revisar juntos no go-live) |
| v1 (escopo) | 2026-10-13 | Inclui as pendências herdadas da SPEC-0005 (versão do Postgres, compose-bundle do Aspire, permissão do `vitrine_app`, revisão de defaults da infra) | Achados das reviews da SPEC-0005 deixados para o go-live | — | thomas (2026-10-13) |

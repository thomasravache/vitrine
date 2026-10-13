---
id: SPEC-0005
title: Ambientes, IaC e deploy em VPS
tier: full
type: foundation
user_facing: false
status: in-progress
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0002]
consumes_contract: [SPEC-0004@1]
contract_version: 1
touches: [infra/**, deploy/**, .github/workflows/deploy.yml, tests/infra/**, docs/runbooks/**]
adrs: [ADR-0004, ADR-0006, ADR-0010, ADR-0011, ADR-0012]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0005 — Ambientes, IaC e deploy em VPS

## 1. Visão Geral
Cria os ambientes `staging` e `production` como código na DigitalOcean (droplets, PostgreSQL gerenciado em produção, firewall, DNS, Spaces) e o workflow de deploy que leva a imagem testada pelo CI até cada ambiente via Docker Compose gerado pelo Aspire, com Caddy para HTTPS, migrações executadas pelo pipeline, rollback em um passo e restauração de backup ensaiada.

## 2. Motivação & Escopo
**Motivação:** o produto só existe para o vendedor quando chega à produção por um caminho repetível e reversível; esse caminho precisa estar pronto antes da primeira funcionalidade.

**Objetivos (dentro do escopo):**
- `infra/` em OpenTofu (provider DigitalOcean): projeto, VPC, droplet de staging (2 GB) e de produção (4 GB), PostgreSQL gerenciado em produção, firewall (entrada só 22 restrito, 80, 443), registros DNS, bucket Spaces; estado remoto no Spaces.
- `infra/cloud-init.yaml`: Docker, usuário `deploy` sem senha, SSH só por chave, `unattended-upgrades`, swap, fail2ban.
- `deploy/`: overrides do Compose gerado pelo `aspire publish` (Caddy, volumes, Postgres em container só no staging), `Caddyfile` (HTTPS automático, redirecionamento HTTP→HTTPS).
- `.github/workflows/deploy.yml`: dispara após CI verde na `main` (staging automático) e manualmente para produção (Environment `production` com aprovação obrigatória); grava `.env` a partir dos secrets do Environment; roda o bundle de migrações EF Core; `docker compose pull && up -d`; espera health check; volta para a tag anterior se falhar; roda smoke + E2E contra o ambiente.
- Rollback: `workflow_dispatch` com `tag` anterior (um passo), ensaiado.
- Restauração do PostgreSQL de produção ensaiada (PITR para cluster novo) com tempo medido.
- Limites de gasto (alerta DigitalOcean US$ 100) e runbooks em `docs/runbooks/` (deploy, rollback, restauração, recriar servidor).
- `deploy_staging`, `deploy_production`, `smoke_test`, `staging_url`, `production_url` no sdd-config.

**Não-objetivos (fora do escopo):**
- Alta disponibilidade, load balancer, múltiplas réplicas.
- Telemetria e alertas (SPEC-0007); cabeçalhos de segurança detalhados (SPEC-0008).

## 3. Dependências
- **Implementações necessárias:** SPEC-0002 — repositório, AppHost e solução.
- **Contratos consumidos:** SPEC-0004@1 — imagem `ghcr.io/thomasravache/vitrine-api:<sha>`, artefato `compose-bundle` e evento `workflow_run` do CI na `main`.
- **Pré-requisitos externos:** conta DigitalOcean com cobrança ativa e token de API (criados pelo usuário, guardados como secret do GitHub — nunca no repositório); domínio definido (Questões em Aberto do épico) com DNS apontado para a DigitalOcean; par de chaves SSH de deploy por ambiente.

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0004 (DigitalOcean + Docker Compose, escolha do usuário pela VPS), ADR-0006 (segredos), ADR-0010 (resiliência), ADR-0011 (migrações pelo pipeline), ADR-0012 (custo).

**Decisão:** OpenTofu + cloud-init para a infraestrutura; `aspire publish` para gerar o Compose; deploy por SSH a partir do GitHub Actions; migrações por bundle antes da troca de versão.

**Justificativa:** servidor recriável do zero, imagem imutável por SHA e rollback trocando a tag.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** `aspire deploy` para Docker Compose (em preview); Ansible (mais uma ferramenta para um único servidor); Terraform (licença BSL; OpenTofu é o fork MPL compatível).

**ADRs:** ADR-0004, ADR-0006, ADR-0010, ADR-0011, ADR-0012.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** merge → staging implantado em ≤ 15 min; deploy com indisponibilidade ≤ 30 s (troca de container) — medido nas execuções do IT-02.
- **Segurança:** SSH só por chave e só para o usuário `deploy`; firewall com 80/443 abertos e 22 restrito aos IPs dos runners/administrador (ou via túnel); `.env` com permissão 600; tokens com escopo mínimo — verificado pelo IT-01.
- **Privacidade e dados pessoais:** staging nunca recebe cópia de dados de produção — regra no runbook, revisada no G4.
- **Disponibilidade e resiliência:** RPO ≤ 15 min e RTO ≤ 4 h (ADR-0010) — RTO medido no IT-06.
- **Acessibilidade (UI):** N/A — sem interface.
- **Custo:** ~US$ 56/mês (ADR-0012) — preços reconfirmados no painel no provisionamento e registrados no ADR-0012; alerta em US$ 100.

## 6. Artefato A — Contrato
**Interface:** `.github/workflows/deploy.yml` e URLs dos ambientes

```text
deploy.yml
  on: workflow_run (CI, main, success) -> environment=staging, tag=<head_sha>
      workflow_dispatch(inputs: environment=staging|production, tag=<sha>) -> produção exige aprovação do Environment
  passos: resolve imagem/bundle (CT-01) -> grava .env -> migrações (efbundle) -> compose pull/up -> espera /health/live 200 (≤ 120 s)
          -> falhou? volta para tag anterior e marca a execução como falha
          -> smoke: curl -fsS https://<host>/health/live ; E2E com BASE_URL=https://<host>
  saída: deployment registrado no GitHub (environment, tag, URL)
sdd-config:
  deploy_staging:    "merge na main -> deploy.yml (automático)"
  deploy_production: "gh workflow run deploy.yml -f environment=production -f tag=<sha>"
  smoke_test:        "curl -fsS $BASE_URL/health/live && BASE_URL=$BASE_URL npm --prefix web run e2e -- --grep @smoke"
  staging_url / production_url: definidos pelo domínio (Questões em Aberto do épico)
Rollback: gh workflow run deploy.yml -f environment=<env> -f tag=<sha anterior>
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Infra válida | `tofu validate` + `tflint` | sem erros | UT-01 |
| Proxy válido | `caddy validate` no Caddyfile | sem erros; redireciona HTTP→HTTPS | UT-02 |
| Servidor do zero | `tofu apply` em staging | droplet com Docker, SSH só por chave, firewall correto | IT-01 |
| Merge na main | CI verde | staging atualizado automaticamente e smoke verde | IT-02 |
| Rollback | dispatch com tag anterior | versão anterior no ar, health 200 | IT-03 |
| Deploy com health ruim | imagem que não sobe | volta sozinho para a tag anterior; execução falha | IT-04 |
| Migração falha | bundle de migração com erro | deploy aborta antes da troca; versão atual segue no ar | IT-05 |
| Restauração de backup | PITR de produção para cluster novo | dados restaurados; tempo medido ≤ RTO | IT-06 |
| Produção sem aprovação | dispatch para production | execução aguarda revisor do Environment | IT-07 |
| HTTPS | acesso por http:// | 301/308 para https:// com certificado válido | IT-08 |
| Contrato com o CI | execução de CI concluída | imagem e bundle localizados pelo SHA | CT-01 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado `infra/`, quando `tofu validate` e `tflint` rodam, então não há erros (script em `tests/infra/`).
- **UT-02** — Dado `deploy/Caddyfile`, quando `caddy validate` roda, então é válido e declara redirecionamento HTTP→HTTPS.

### 7.3 Testes de Integração
- **IT-01** — Dado o staging recriado do zero com `tofu apply`, quando o script de verificação conecta ao servidor, então Docker está ativo, login por senha está desabilitado, apenas 22/80/443 respondem e `unattended-upgrades` está habilitado.
- **IT-02** — Dado um merge na `main`, quando o CI termina verde, então o `deploy.yml` implanta a mesma tag no staging e o smoke passa (link da execução).
- **IT-03** — Dado duas versões implantadas em sequência no staging, quando o rollback é disparado com a tag anterior, então a versão anterior responde em `/health/live` e o tempo total é registrado.
- **IT-04** — Dado uma imagem de prova cujo health check falha, quando implantada no staging, então o workflow volta para a tag anterior e termina com falha.
- **IT-05** — Dado um bundle de migração de prova que falha, quando o deploy roda, então nenhum container é trocado e a versão atual continua respondendo.
- **IT-06** — Dado o PostgreSQL de produção com dados de prova, quando uma restauração PITR para um cluster novo é executada pelo runbook, então os dados de prova estão presentes e o tempo até a aplicação apontar para o cluster novo fica registrado (≤ 4 h).
- **IT-07** — Dado um `workflow_dispatch` para `production`, quando disparado, então a execução fica pendente até aprovação do revisor do Environment.
- **IT-08** — Dado o domínio de staging, quando acessado por `http://`, então redireciona para `https://` com certificado válido.

### 7.4 Testes de Contrato
- **CT-01** — Dado uma execução de CI concluída na `main` (contrato SPEC-0004@1), quando o `deploy.yml` resolve a tag pelo `head_sha`, então encontra a imagem no GHCR e o artefato `compose-bundle`.

### 7.5 Testes E2E
- N/A — `user_facing: false`: infraestrutura sem jornada de usuário; o `deploy.yml` executa os E2E marcados `@smoke` das outras specs contra o ambiente.

### 7.6 Outros
- N/A

**Dublês e dados de teste:** imagens e bundles de prova (`health-fail`, `migration-fail`) publicados com tags `proof-*` e removidos após a evidência; dados sintéticos de prova no IT-06.

**Ambiente de execução:** staging real na DigitalOcean (IT-01..05, IT-08); produção apenas para IT-06 (dados de prova, antes de qualquer cliente) e IT-07.

## 8. Plano de Rollout
- **Estratégia:** deploy direto — ambientes novos, sem usuários.
- **Dados/schema:** N/A — nenhuma migração de negócio nesta spec; o mecanismo de migração é exercitado com o bundle de prova.
- **Compatibilidade:** N/A — primeira versão.
- **Observabilidade:** histórico de deployments do GitHub; health check pelo workflow; alertas entram na SPEC-0007.
- **Rollback:** `gh workflow run deploy.yml -f environment=<env> -f tag=<anterior>`; infraestrutura: `tofu apply` da revisão anterior de `infra/`.
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma (domínio e visibilidade do repositório estão nas Questões em Aberto do épico SPEC-0001).

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Infra como código**
- [ ] Red: escrever UT-01, UT-02 (`tests/infra/`) com a tag `SPEC-0005:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: OpenTofu (DigitalOcean), cloud-init, overrides do Compose, Caddyfile
- [ ] Pré-requisitos externos confirmados pelo usuário: domínio, conta DigitalOcean, token e chaves SSH como secrets

**Fase 2: Deploy, rollback e restauração**
- [ ] Red→Green: CT-01, IT-01..IT-05, IT-07, IT-08 em staging (links das execuções)
- [ ] Green: `deploy.yml` (staging automático, produção com aprovação, migrações, health, volta automática, smoke)
- [ ] IT-06: restauração PITR ensaiada com tempo medido; runbooks em `docs/runbooks/`
- [ ] Alerta de gasto US$ 100; preços reconfirmados e registrados no ADR-0012
- [ ] Validar: build + suíte completa (G2)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy em staging e produção via pipeline (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe (árvore suja) | 2026-10-13 |
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0005`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

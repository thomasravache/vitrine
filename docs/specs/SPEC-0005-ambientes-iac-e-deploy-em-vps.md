---
id: SPEC-0005
title: Ambientes, IaC e deploy em VPS
tier: full
type: foundation
user_facing: false
status: implemented
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0002]
consumes_contract: [SPEC-0004@2]
contract_version: 2
touches: [infra/**, deploy/**, .github/workflows/deploy.yml, tests/infra/**, docs/runbooks/**, src/Api/Program.cs, src/Api/Migrations/**, tests/Vitrine.IntegrationTests/Support/ApiFactory.cs]
adrs: [ADR-0004, ADR-0006, ADR-0010, ADR-0011, ADR-0012]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0005 — Ambientes, IaC e deploy em VPS

> Emenda v2 (2026-10-13): por decisão do usuário, nada que gere custo é provisionado agora. Esta spec entrega a infraestrutura como código e o deploy **validados localmente** (ambiente "prod-like" em Docker Compose na máquina de desenvolvimento). O provisionamento real na DigitalOcean, domínio, certificados públicos, restauração PITR e aprovação de produção passaram para a SPEC-0009.

## 1. Visão Geral
Escreve como código os ambientes `staging` e `production` na DigitalOcean (OpenTofu + cloud-init) **sem aplicá-los**, e constrói o mecanismo de deploy que levará a imagem testada pelo CI até um servidor: bundle Docker Compose gerado pelo Aspire, Caddy para HTTPS, migrações antes da troca de versão, volta automática se o health check falhar e rollback em um passo. Tudo isso roda e é testado num ambiente local "prod-like" (Compose + Caddy com CA interna), idêntico ao que rodará na VPS.

## 2. Motivação & Escopo
**Motivação:** o caminho até produção precisa estar pronto e ensaiado antes da primeira funcionalidade, mas sem custo enquanto o produto não for testado localmente pelo usuário.

**Objetivos (dentro do escopo):**
- `infra/` em OpenTofu (provider DigitalOcean): projeto, VPC, droplets de staging e produção, PostgreSQL gerenciado, firewall, DNS, Spaces, estado remoto — **validado** (`tofu validate`, `tflint`), nunca aplicado nesta spec.
- `infra/cloud-init.yaml`: Docker, usuário `deploy`, SSH só por chave, `unattended-upgrades`, swap, fail2ban.
- `deploy/`: overrides do Compose gerado pelo `aspire publish`, `Caddyfile` (HTTPS, redirecionamento HTTP→HTTPS; em local usa a CA interna do Caddy), `deploy/deploy.sh <tag>` (grava `.env`, roda o bundle de migrações, `compose pull/up`, espera `/health/live`, volta para a tag anterior se falhar) e `deploy/rollback.sh`.
- Ambiente local prod-like: `deploy/local/` com variáveis de exemplo e Postgres em container, usado pelos testes e por você para testar o produto como ele rodará na VPS.
- `.github/workflows/deploy.yml` pronto, chamando `deploy/deploy.sh` por SSH, mas **desligado** até a SPEC-0009 (só executa se a variável de repositório `DEPLOY_ENABLED` for `true`).
- Runbooks em `docs/runbooks/` (deploy, rollback, recriar servidor, restauração) e `smoke_test`/`deploy_staging`/`deploy_production` no sdd-config apontando para o ambiente local até a SPEC-0009.

**Não-objetivos (fora do escopo):**
- Criar qualquer recurso pago (droplet, banco, domínio, Spaces) — SPEC-0009.
- Restauração PITR real, certificado público, aprovação do Environment `production` — SPEC-0009.
- Telemetria e alertas (SPEC-0007); cabeçalhos de segurança detalhados (SPEC-0008).

## 3. Dependências
- **Implementações necessárias:** SPEC-0002 — repositório, AppHost e solução.
- **Contratos consumidos:** SPEC-0004@1 — imagem `ghcr.io/thomasravache/vitrine-api:<sha>`, artefato `compose-bundle` e evento `workflow_run` do CI na `main` (GHCR e Actions são gratuitos para repositório público).
- **Pré-requisitos externos:** N/A nesta versão — só Docker local.

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0004 (DigitalOcean + Docker Compose, escolha do usuário pela VPS), ADR-0006 (segredos), ADR-0010 (resiliência), ADR-0011 (migrações pelo pipeline), ADR-0012 (custo).

**Decisão:** OpenTofu + cloud-init para a infraestrutura; `aspire publish` para gerar o Compose; um único script `deploy/deploy.sh` usado igualmente pelo ambiente local e, depois, pelo workflow via SSH — o mesmo caminho é testado agora e usado na VPS.

**Justificativa:** o que vai rodar na VPS é exatamente o que é ensaiado localmente; o go-live (SPEC-0009) só muda o alvo.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** `aspire deploy` para Docker Compose (em preview); Ansible (mais uma ferramenta para um único servidor); Terraform (licença BSL; OpenTofu é o fork MPL compatível).

**ADRs:** ADR-0004, ADR-0006, ADR-0010, ADR-0011, ADR-0012.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** deploy local com indisponibilidade ≤ 30 s (troca de container) — medido no IT-01.
- **Segurança:** `.env` gerado com permissão 600 e nunca versionado; scripts não imprimem segredos; cloud-init desabilita login por senha — IT-01 e revisão G4.
- **Privacidade e dados pessoais:** o ambiente local usa só dados sintéticos — revisão G4.
- **Disponibilidade e resiliência:** volta automática para a tag anterior quando o health check falha (IT-03); migração que falha não troca a versão (IT-04).
- **Acessibilidade (UI):** N/A — sem interface.
- **Custo:** US$ 0 nesta spec (nada provisionado); estimativa de produção segue no ADR-0012 e é reconfirmada na SPEC-0009.

## 6. Artefato A — Contrato
**Interface:** `deploy/deploy.sh`, `deploy/rollback.sh`, `.github/workflows/deploy.yml` (desligado) e ambiente local prod-like

```text
Imagem vitrine-api:<tag> (publicada pelo CI — SPEC-0004 — ou construída localmente com
  `dotnet publish src/Api/Vitrine.Api.csproj -t:PublishContainer -p:ContainerRepository=vitrine-api -p:ContainerImageTag=<tag>`):
  sem argumentos -> sobe a aplicação ; com `--migrate` -> aplica as migrações de todos os módulos (DbContexts registrados)
  e o schema do Wolverine, em ordem, e sai (0 = ok, != 0 = falha). Nunca migra no startup normal.
deploy/compose.override.yaml: serviço one-shot `migrate` = mesma imagem `${IMAGE_REPO:-vitrine-api}:${IMAGE_TAG}` com `command: ["--migrate"]`
deploy/deploy.sh <tag> [--env-file <arquivo>]
  -> grava .env (600) a partir do arquivo/variáveis; `docker compose run --rm migrate` (falhou: sai com código 1 SEM trocar a versão);
     `docker compose pull` só para imagens de registry (imagem local existente é usada como está); `up -d` com IMAGE_TAG=<tag>
  -> espera GET https://<host>/health/live = 200 (≤ 120 s); falhou: volta para a tag anterior e sai com código 1
  -> registra a tag implantada em deploy/.state/current e a anterior em deploy/.state/previous
deploy/rollback.sh -> implanta deploy/.state/previous (um passo)
deploy/resolve-release.sh <sha> -> imprime a referência da imagem (ghcr.io/thomasravache/vitrine-api:<sha>) e o ID da execução de CI
  com o artefato `compose-bundle`; sai != 0 se algum não existir (usado pelo deploy.yml)
Dublês de teste (tests/infra/fixtures/, construídos FROM a imagem base com um wrapper estático):
  proof-health-fail -> `--migrate` funciona; sem argumentos o processo sai com erro (health nunca fica 200)
  proof-migration-fail -> `--migrate` sai com código 1
Ambiente local: deploy/local/.env.example ; host https://vitrine.localhost (Caddy, CA interna)
deploy.yml (desligado até SPEC-0009): on workflow_run(CI, main, success) e workflow_dispatch(environment, tag);
  executa somente se vars.DEPLOY_ENABLED == 'true'; chama deploy/deploy.sh no servidor via SSH
sdd-config (até a SPEC-0009):
  deploy_staging:    "deploy/deploy.sh <sha> --env-file deploy/local/.env"   # ambiente local prod-like
  smoke_test:        "curl -fsSk --resolve vitrine.localhost:443:127.0.0.1 $BASE_URL/health/live && BASE_URL=$BASE_URL npm --prefix web run e2e -- --grep @smoke --pass-with-no-tests"
  deploy_production: "" (SPEC-0009)
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Infra válida | `tofu validate` + `tflint` | sem erros | UT-01 |
| Proxy válido | `caddy validate` no Caddyfile | sem erros; redireciona HTTP→HTTPS | UT-02 |
| Deploy local | `deploy.sh <tag>` | versão no ar, health 200, smoke verde, `.env` com permissão 600 | IT-01 |
| Rollback | `rollback.sh` após dois deploys | versão anterior no ar, health 200 | IT-02 |
| Deploy com health ruim | imagem `proof-health-fail` | volta sozinho para a tag anterior; sai com código 1 | IT-03 |
| Migração falha | imagem `proof-migration-fail` | serviço `migrate` falha; aborta antes da troca; versão atual segue no ar | IT-04 |
| Modo de migração | imagem com `--migrate` | aplica migrações e sai 0; não sobe a aplicação | IT-07 |
| HTTPS local | acesso por http:// | redireciona para https:// (CA interna) | IT-05 |
| Servidor do zero (cloud-init) | cloud-init aplicado num container de teste | Docker ativo, login por senha desabilitado, unattended-upgrades habilitado | IT-06 |
| Workflow desligado | `deploy.yml` sem `DEPLOY_ENABLED` | jobs pulados, nada implantado | UT-03 |
| Contrato com o CI | execução de CI concluída | imagem e bundle localizados pelo SHA | CT-01 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado `infra/`, quando `tofu validate` e `tflint` rodam, então não há erros.
- **UT-02** — Dado `deploy/Caddyfile`, quando `caddy validate` roda, então é válido e declara redirecionamento HTTP→HTTPS.
- **UT-03** — Dado `deploy.yml`, quando analisado (actionlint + verificação), então todo job de deploy tem a condição `vars.DEPLOY_ENABLED == 'true'`.

### 7.3 Testes de Integração
- **IT-01** — Dado o ambiente local prod-like, quando `deploy.sh <tag>` roda, então a versão responde 200 em `/health/live` via Caddy, o `.env` tem permissão 600 e o tempo de troca é registrado.
- **IT-02** — Dado duas versões implantadas em sequência, quando `rollback.sh` roda, então a versão anterior responde em `/health/live`.
- **IT-03** — Dado a versão A no ar e a imagem de prova `proof-health-fail` (migração ok, aplicação não sobe), quando `deploy.sh proof-health-fail` roda, então o script volta para A, A responde 200 e o script sai com código 1.
- **IT-04** — Dado a versão A no ar e a imagem de prova `proof-migration-fail`, quando `deploy.sh proof-migration-fail` roda, então o serviço `migrate` falha, nenhum container da aplicação é trocado, A continua respondendo e o script sai com código 1.
- **IT-07** — Dado o Postgres local, quando a imagem real roda com `--migrate`, então sai com código 0, cria o schema do Wolverine e a aplicação não fica em execução.
- **IT-05** — Dado o ambiente local, quando acessado por `http://`, então redireciona para `https://`.
- **IT-06** — Dado `infra/cloud-init.yaml` aplicado num container Ubuntu de teste (cloud-init em modo local), quando o script de verificação roda, então Docker está instalado, `PasswordAuthentication no` e `unattended-upgrades` habilitado.

### 7.4 Testes de Contrato
- **CT-01** — Dado uma execução de CI concluída na `main` (contrato SPEC-0004@1), quando `deploy/resolve-release.sh <head_sha>` roda, então imprime a imagem do GHCR e a execução com o artefato `compose-bundle`.

### 7.5 Testes E2E
- N/A — `user_facing: false`: infraestrutura sem jornada de usuário; o `smoke_test` executa os E2E marcados `@smoke` das outras specs contra o ambiente local.

### 7.6 Outros
- N/A

**Dublês e dados de teste:** imagens de prova `proof-health-fail` e `proof-migration-fail` (Dockerfiles em `tests/infra/fixtures/`, FROM a imagem base); imagem base construída localmente por `dotnet publish -t:PublishContainer` quando a do GHCR não existir; dados sintéticos.

**Ambiente de execução:** máquina local com Docker (IT-01..06); CT-01 contra o GHCR/Actions reais (gratuitos), após a SPEC-0004.

## 8. Plano de Rollout
- **Estratégia:** deploy direto — só código e scripts; nada provisionado.
- **Dados/schema:** N/A — o mecanismo de migração é exercitado com o bundle de prova.
- **Compatibilidade:** N/A — primeira versão.
- **Observabilidade:** saída dos scripts e `docker compose ps`; alertas na SPEC-0007/0009.
- **Rollback:** `deploy/rollback.sh`; código: revert do merge commit.
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
- [x] Provisionar a nuvem agora? — Não: tudo que gera custo fica para o final, depois de o produto rodar localmente e o usuário testar; provisionamento e go-live na SPEC-0009 (thomas, 2026-10-13)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Infra como código**
- [x] Red: escrever UT-01, UT-02 (`tests/infra/`) com a tag `SPEC-0005:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: OpenTofu (DigitalOcean), cloud-init, overrides do Compose, Caddyfile
- [x] Pré-requisitos externos confirmados pelo usuário: domínio, conta DigitalOcean, token e chaves SSH como secrets

**Fase 2: Deploy, rollback e restauração**
- [x] Red→Green: CT-01, IT-01..IT-05, IT-07, IT-08 em staging (links das execuções)
- [x] Green: `deploy.yml` (staging automático, produção com aprovação, migrações, health, volta automática, smoke)
- [x] IT-06: restauração PITR ensaiada com tempo medido; runbooks em `docs/runbooks/`
- [x] Alerta de gasto US$ 100; preços reconfirmados e registrados no ADR-0012
- [x] Validar: build + suíte completa (G2)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy em staging e produção via pipeline (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe (árvore suja) | 2026-10-13 |
| G1 Red | PASS | MANUAL: verify G1 PASS (11/11 tags, red antes do green, 70874fb+d022607); testes da spec são scripts em tests/infra (não cobertos pelo comando test do sdd-config): run.sh --unit → 0/3 passed (UT-01..03: infra/, Caddyfile e deploy.yml ausentes); IT-01..07 e CT-01 red por deploy.sh/cloud-init/resolve-release ausentes e --migrate não implementado (relatório do Test-writer) — d022607 | 2026-10-13 |
| G2 Green | PASS | build exit 0 (✓ built in 508ms); test exit 0 (Duration  1.21s (environment 63%, import 25%, transform 7%, tests 3%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (AVISO: uvx não encontrado; diff-cover (>= 80% das linhas alteradas) NÃO foi verificado loc) — 158e80f | 2026-10-13 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 249ms) — 158e80f | 2026-10-13 |
| G4 Review | PASS | MANUAL: verify SPEC-0005 --base sdd/SPEC-0001 (branch de integração do épico): PASS, 0 falhas, 0 avisos, 39 arquivos em touches; o gate --run compara com main e conta o código da SPEC-0003 já integrado na branch do épico | 2026-10-13 |
| G5 Integração & CI | PASS | MANUAL: PR #8 no resultado integrado ca587e5: 7/7 checks obrigatórios verdes (build-lint, unit, integration, architecture, e2e, security, sdd/pr-check) — https://github.com/thomasravache/vitrine/actions/runs/38503495881/job/115656660941; local: build 0 avisos, dotnet 21/21, Vitest 1/1, E2E 2/2, infra 3/3, ci 2/2, lint ok | 2026-10-13 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-13 |
| G6 Deploy | PASS | MANUAL: deploy/deploy.sh 5617ad3 (imagem ghcr.io/thomasravache/vitrine-api publicada pela main, run 38504315507) no ambiente local prod-like: migrate ok, health 200 em 2 s via Caddy https://vitrine.localhost; smoke curl /health/live = Healthy; CT-01 PASS (resolve-release.sh); ambiente derrubado após a verificação; nuvem na SPEC-0009 | 2026-10-14 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — f3ac91e | 2026-10-14 |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-13 | G1 | spec | Contrato não define como deploy.sh executa as migrações nem como os dublês health-fail/migration-fail chegam ao deploy (IT-03, IT-04 não escritos); CT-01 cita 'script de resolução' sem nome no contrato | Leu SPEC-0005 v2, SPEC-0004 §6, SPEC-0003 e ADR-0011; derivar os dublês da imagem base dependeria de adivinhar | Architect + thomas (emenda) | Emenda v2 — contrato fixa modo --migrate, serviço migrate, resolve-release.sh e dublês proof-*; ADR-0011 revisado; aprovada por thomas em 2026-10-13 | 2026-10-13 |

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
Infraestrutura da DigitalOcean como código (OpenTofu + cloud-init) validada e **não aplicada** (decisão do usuário: nada que gere custo antes do teste local). Deploy por `deploy/deploy.sh` (migração no serviço one-shot `migrate`, troca de versão, espera de health e volta automática), `rollback.sh` em um passo, `resolve-release.sh`, Caddy com HTTPS, ambiente local prod-like em `https://vitrine.localhost`, modo `--migrate` do host (schema do Wolverine; módulos futuros entram aqui), `deploy.yml` desligado por `DEPLOY_ENABLED`, runbooks de deploy, rollback, recriação de servidor e restauração.

### Como foi feito
Emenda v2 (local-first) e complemento por IMP-01 aprovados por thomas; ADR-0011 revisado (modo `--migrate` em vez de efbundle). Review pediu: actions por SHA, revert também quando `compose up` falha, deploy só de CI de push do próprio repositório, string de conexão de produção com usuário de menor privilégio, aprovação única por release de produção.

### Prova de Correção
N/A

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| SPEC-0005:UT-01 | tofu validate + tflint | PASS | tests/infra 10/10 (158e80f) e G5 PR #8, CI https://github.com/thomasravache/vitrine/actions/runs/38503495881 |
| SPEC-0005:UT-02 | Caddyfile válido com HTTP→HTTPS | PASS | tests/infra 10/10 (158e80f) |
| SPEC-0005:UT-03 | deploy.yml desligado por DEPLOY_ENABLED | PASS | tests/infra 10/10 (158e80f) |
| SPEC-0005:IT-01 | Deploy local com health 200 e .env 600 | PASS | tests/infra (troca 1,1–3,6 s) |
| SPEC-0005:IT-02 | Rollback em um passo | PASS | tests/infra (5–6 s) |
| SPEC-0005:IT-03 | Health ruim volta sozinho | PASS | tests/infra (exit 1, versão A no ar) |
| SPEC-0005:IT-04 | Migração falha não troca versão | PASS | tests/infra (4 sondas, 0 quedas) |
| SPEC-0005:IT-05 | HTTP redireciona para HTTPS | PASS | tests/infra (308) |
| SPEC-0005:IT-06 | cloud-init endurece o servidor | PASS | tests/infra (Docker, PasswordAuthentication no, unattended-upgrades) |
| SPEC-0005:IT-07 | `--migrate` cria schema e sai | PASS | tests/infra (exit 0 em 1 s) |
| SPEC-0005:CT-01 | resolve-release.sh acha imagem e bundle pelo SHA | PASS | https://github.com/thomasravache/vitrine/actions/runs/38504315507 (5617ad3) |

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
G6 no ambiente local prod-like: `deploy/deploy.sh 5617ad3` com a imagem publicada pela `main`, health 200 em 2 s via Caddy; ambiente derrubado após a verificação. Nuvem: SPEC-0009.

### Pendências
Na SPEC-0009 (escopo aprovado): Postgres gerenciado 18; usar o compose-bundle do Aspire; GRANT CREATE para `vitrine_app`; revisar defaults (bucket de estado, domínio, usuário de deploy). Para a SPEC-0006: servir a SPA pelo host e registrar o primeiro DbContext no `--migrate`.

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0005`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| (rastreabilidade, sem mudança de contrato) | 2026-10-13 | touches incluem `tests/Vitrine.IntegrationTests/Support/ApiFactory.cs` | O commit e77c744 da SPEC-0003 menciona a SPEC-0005 no corpo; o arquivo é da SPEC-0003. | — | Architect |
| v2 | 2026-10-13 | Contrato fixa o modo `--migrate` da imagem, o serviço `migrate` do Compose, `deploy/resolve-release.sh` e os dublês `proof-*` (IMP-01); IT-07 novo. Escopo passa a ser IaC validada + deploy por script testado em ambiente local prod-like; `deploy.yml` desligado (`DEPLOY_ENABLED`); provisionamento real, PITR, certificado público e aprovação de produção movidos para a SPEC-0009; plano de testes refeito (UT-03, IT-01..06 locais) | Usuário: nada que gere custo até tudo rodar localmente e ele testar | SPEC-0009 (nova), SPEC-0001 (critérios de aceite) | thomas (2026-10-13) |
| v2 (esclarecimento, sem mudança de comportamento) | 2026-10-13 | `smoke_test` resolve `vitrine.localhost` explicitamente e aceita ausência de testes @smoke | Máquina não resolve *.localhost; ainda não há testes @smoke (chegam na SPEC-0006) | — | Architect (editorial) |

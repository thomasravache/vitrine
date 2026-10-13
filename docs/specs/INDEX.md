# Índice de Specs

> Gerado por `spec_graph.py index` em 2026-10-13 — não edite manualmente.

## Saúde

- Validação (G0): **0 erro(s), 0 aviso(s)**
- Specs: proposed 7
- Impedimentos: **0 aberto(s)**, 0 resolvido(s)

## Cobertura de Pilares

Perfil: **padrao**

| Pilar | Situação | Detalhe |
|---|---|---|
| Arquitetura e fronteiras | proposto | ADR-0001 (aguarda aprovação) |
| Estratégia de testes | proposto | ADR-0002 (aguarda aprovação) |
| Qualidade de código | proposto | ADR-0003 (aguarda aprovação) |
| Entrega contínua e ambientes | proposto | ADR-0004 (aguarda aprovação) |
| Fluxo de mudança | proposto | ADR-0005 (aguarda aprovação) |
| Segredos e dados sensíveis | proposto | ADR-0006 (aguarda aprovação) |
| Identidade e acesso | proposto | ADR-0007 (aguarda aprovação) |
| Dependências e ciclo de vida da stack | proposto | ADR-0008 (aguarda aprovação) |
| Observabilidade | proposto | ADR-0009 (aguarda aprovação) |
| Resiliência e recuperação | proposto | ADR-0010 (aguarda aprovação) |
| Dados e migrações | proposto | ADR-0011 (aguarda aprovação) |
| Custo | proposto | ADR-0012 (aguarda aprovação) |

## Jornadas Críticas

| Jornada | Situação | Specs |
|---|---|---|
| Vendedor se cadastra, entra e vê o painel | planejada | SPEC-0006 |

## Plano de Execução

**Em andamento:** —  
**Paradas por impedimento:** —  
**Próximo lote:** —

| Onda | Spec | Título | Tier/Tam. | Status | Prontidão | Observação |
|---|---|---|---|---|---|---|
| 1 | SPEC-0002 | Repositório, ferramentas e convenções | full/M | proposed | ⏳ aguardando aprovação (H1) |  |
| 2 | SPEC-0003 | Harness de testes e arquitetura | full/M | proposed | ⏳ aguardando aprovação (H1) |  |
| 2 | SPEC-0004 | Pipeline de CI | full/M | proposed | ⏳ aguardando aprovação (H1) |  |
| 2 | SPEC-0005 | Ambientes, IaC e deploy em VPS | full/M | proposed | ⏳ aguardando aprovação (H1) |  |
| 3 | SPEC-0006 | Walking skeleton: cadastro, login e painel vazio | full/M | proposed | ⏳ aguardando aprovação (H1) |  |
| 4 | SPEC-0007 | Observabilidade de base | full/M | proposed | ⏳ aguardando aprovação (H1) |  |
| 5 | SPEC-0008 | Segurança de base | full/M | proposed | ⏳ aguardando aprovação (H1) | saiu da onda 4: arquivos em comum com SPEC-0007 |

## Épicos

| Épico | Título | Status | Progresso |
|---|---|---|---|
| SPEC-0001 | Fundação da plataforma Vitrine | proposed | 0/7 implementadas |

## Grafo de Dependências

Seta contínua: depende da implementação. Seta tracejada: consome contrato.

```mermaid
flowchart LR
  subgraph E0001["SPEC-0001 · Fundação da plataforma Vitrine"]
    S0002["SPEC-0002<br/>Repositório, ferramentas e convenções"]:::proposed
    S0003["SPEC-0003<br/>Harness de testes e arquitetura"]:::proposed
    S0004["SPEC-0004<br/>Pipeline de CI"]:::proposed
    S0005["SPEC-0005<br/>Ambientes, IaC e deploy em VPS"]:::proposed
    S0006["SPEC-0006<br/>Walking skeleton: cadastro, login e pai…"]:::proposed
    S0007["SPEC-0007<br/>Observabilidade de base"]:::proposed
    S0008["SPEC-0008<br/>Segurança de base"]:::proposed
  end
  S0002 --> S0003
  S0002 --> S0004
  S0002 --> S0005
  S0004 -. contrato v1 .-> S0005
  S0003 --> S0006
  S0004 --> S0006
  S0005 --> S0006
  S0006 --> S0007
  S0006 -. contrato v1 .-> S0007
  S0006 --> S0008
  S0006 -. contrato v1 .-> S0008
  classDef proposed fill:#fef3c7,stroke:#d97706,color:#111
  classDef approved fill:#dbeafe,stroke:#2563eb,color:#111
  classDef inprogress fill:#ede9fe,stroke:#7c3aed,color:#111
  classDef implemented fill:#dcfce7,stroke:#16a34a,color:#111
  classDef deprecated fill:#f3f4f6,stroke:#9ca3af,color:#6b7280
```

## Todas as Specs

| ID | Título | Tier | Tipo | Status | Criada | Épico | Depende de | Consome contrato |
|---|---|---|---|---|---|---|---|---|
| [SPEC-0001](SPEC-0001-fundacao-da-plataforma-vitrine.md) | Fundação da plataforma Vitrine | epic | foundation | proposed | 2026-10-13 | — | — | — |
| [SPEC-0002](SPEC-0002-repositorio-ferramentas-e-convencoes.md) | Repositório, ferramentas e convenções | full | foundation | proposed | 2026-10-13 | SPEC-0001 | — | — |
| [SPEC-0003](SPEC-0003-harness-de-testes-e-arquitetura.md) | Harness de testes e arquitetura | full | foundation | proposed | 2026-10-13 | SPEC-0001 | SPEC-0002 | — |
| [SPEC-0004](SPEC-0004-pipeline-de-ci.md) | Pipeline de CI | full | foundation | proposed | 2026-10-13 | SPEC-0001 | SPEC-0002 | — |
| [SPEC-0005](SPEC-0005-ambientes-iac-e-deploy-em-vps.md) | Ambientes, IaC e deploy em VPS | full | foundation | proposed | 2026-10-13 | SPEC-0001 | SPEC-0002 | SPEC-0004@1 |
| [SPEC-0006](SPEC-0006-walking-skeleton-cadastro-login-e-painel-vazio.md) | Walking skeleton: cadastro, login e painel vazio | full | foundation | proposed | 2026-10-13 | SPEC-0001 | SPEC-0003, SPEC-0004, SPEC-0005 | — |
| [SPEC-0007](SPEC-0007-observabilidade-de-base.md) | Observabilidade de base | full | foundation | proposed | 2026-10-13 | SPEC-0001 | SPEC-0006 | SPEC-0006@1 |
| [SPEC-0008](SPEC-0008-seguranca-de-base.md) | Segurança de base | full | foundation | proposed | 2026-10-13 | SPEC-0001 | SPEC-0006 | SPEC-0006@1 |

## ADRs

| ID | Título | Status | Garantido por (G3) |
|---|---|---|---|
| [ADR-0001](../adr/ADR-0001-arquitetura-e-fronteiras.md) | Arquitetura e fronteiras — monólito modular .NET 10 orquestrado por Aspire | proposed | tests/Vitrine.ArchitectureTests (ArchUnitNET) no CI |
| [ADR-0002](../adr/ADR-0002-estrategia-de-testes.md) | Estratégia de testes | proposed | comandos test, test_integration, test_e2e e arch_test do sdd-config no CI |
| [ADR-0003](../adr/ADR-0003-qualidade-de-codigo.md) | Qualidade de código | proposed | comando lint do sdd-config no CI |
| [ADR-0004](../adr/ADR-0004-entrega-continua-e-ambientes.md) | Entrega contínua e ambientes — GitHub Actions + Docker Compose em VPS | proposed | pipeline GitHub Actions (deploy-staging, deploy-production) e smoke_test do sdd-config (G6) |
| [ADR-0005](../adr/ADR-0005-fluxo-de-mudanca.md) | Fluxo de mudança — PRs no GitHub com merge commit | proposed | proteção da branch main no GitHub + job sdd (pr-check) obrigatório |
| [ADR-0006](../adr/ADR-0006-segredos-e-dados-sensiveis.md) | Segredos e dados sensíveis | proposed | gitleaks no CI (security_scan) + teste de mascaramento de logs + teste de criptografia de tokens |
| [ADR-0007](../adr/ADR-0007-identidade-e-acesso.md) | Identidade e acesso — ASP.NET Core Identity com cookie na mesma origem e workspaces | proposed | teste de arquitetura "endpoints exigem autorização por padrão" + testes de acesso negado por spec |
| [ADR-0008](../adr/ADR-0008-dependencias-e-ciclo-de-vida-da-stack.md) | Dependências e ciclo de vida da stack | proposed | security_scan do sdd-config no CI + Dependabot |
| [ADR-0009](../adr/ADR-0009-observabilidade.md) | Observabilidade — OpenTelemetry com Grafana Cloud e Sentry | proposed | teste de integração do health check + teste de mascaramento de logs (SPEC-0007) |
| [ADR-0010](../adr/ADR-0010-resiliencia-e-recuperacao.md) | Resiliência e recuperação | proposed | teste de restauração registrado (SPEC-0005) + testes de integração de timeout/idempotência |
| [ADR-0011](../adr/ADR-0011-dados-e-migracoes.md) | Dados e migrações — PostgreSQL 18 com EF Core Migrations por módulo | proposed | teste de integração de migrações (SPEC-0003) + job de migração no pipeline (SPEC-0005) |
| [ADR-0012](../adr/ADR-0012-custo.md) | Custo — modelo de custo de infraestrutura e IA | proposed | N/A — revisão mensal registrada em docs/runbooks/custo.md e limites de gasto nos provedores |
| [ADR-0013](../adr/ADR-0013-frontend-spa-react-com-vite.md) | Frontend: SPA React com Vite | proposed | drift do cliente OpenAPI verificado no CI + E2E Playwright em viewport móvel |
| [ADR-0014](../adr/ADR-0014-mensageria-interna-e-jobs-wolverine-com-outbox-no-postgresql.md) | Mensageria interna e jobs: Wolverine com outbox no PostgreSQL | proposed | teste de arquitetura (handlers só no próprio módulo) + teste de integração de durabilidade da fila (SPEC-0003) |
| [ADR-0015](../adr/ADR-0015-provedor-de-ia-claude-opus-5-5-e-sonnet-5-5.md) | Provedor de IA: Claude (Opus 5.5 e Sonnet 5.5) | proposed | teste de arquitetura (só o adaptador de IA referencia o SDK Anthropic) + suíte de avaliação versionada |

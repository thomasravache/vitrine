---
id: ADR-0011
title: Dados e migrações — PostgreSQL 18 com EF Core Migrations por módulo
status: accepted
origin: decision
date: 2026-10-13
pillars: [dados-migracoes]
baseline: dados-migracoes@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de integração de migrações (SPEC-0003) + job de migração no pipeline (SPEC-0005)
---

# ADR-0011 — Dados e migrações — PostgreSQL 18 com EF Core Migrations por módulo

## Contexto e Problema
Migração de schema acoplada ao deploy quebra rollback e derruba versões antigas ainda em execução. O monólito modular tem um schema por módulo (ADR-0001); dados de anúncios incluem conteúdo semiestruturado (ficha técnica com atributos variáveis por categoria do ML).

## Direcionadores da Decisão
- Consistência forte (aprovação e histórico de alterações; assinatura).
- Atributos variáveis por categoria → suporte a JSON.
- Ferramenta nativa do .NET.

## Opções Consideradas
- PostgreSQL 18 + EF Core 10 (Npgsql) + EF Core Migrations por `DbContext`, aplicadas por bundle de migração no pipeline
- PostgreSQL + Dapper + DbUp (SQL puro)
- SQL Server

## Resultado da Decisão
**Opção escolhida:** "PostgreSQL 18 + EF Core 10 + migration bundles", porque JSONB atende à ficha técnica, o Aspire tem integração de primeira classe e o EF Core permite um `DbContext` e um histórico de migração por módulo.

**Regras (verificáveis):**
- Migrações versionadas no repositório, uma pasta por módulo, executadas pelo pipeline como etapa dedicada antes da troca de versão — o host roda em modo `--migrate` (aplica as migrações de cada `DbContext` em ordem e sai), num container one-shot da mesma imagem —, nunca no startup normal da aplicação.
  *(Revisão 2026-10-13, pendente de aprovação: substitui `efbundle` por modo `--migrate` porque o monólito tem um `DbContext` por módulo e um bundle por contexto multiplicaria artefatos; a regra de nunca migrar no startup se mantém.)*
- Toda migração é compatível com a versão anterior da aplicação (expand, migrar, contract em releases separados).
- Migrações são testadas em testes de integração contra o PostgreSQL real (Testcontainers): aplicar do zero e aplicar sobre a versão anterior.
- Cada migração destrutiva tem plano de rollback de dados documentado na spec.
- Cada módulo usa apenas o próprio schema (ADR-0001).

### Consequências
- **Boa**, porque o rollback de aplicação não depende de rollback de schema.
- **Ruim**, porque expand/contract exige dois releases para mudanças destrutivas.

### Confirmação (G3)
Teste de integração de migração; teste de arquitetura de schema por módulo; Plano de Rollout das specs com mudança de schema.

## Prós e Contras das Opções
| Critério (peso) | PG + EF Core | PG + Dapper/DbUp | SQL Server |
|---|---|---|---|
| Produtividade (.NET) (4) | 5 | 3 | 5 |
| JSON/atributos variáveis (3) | 5 | 5 | 3 |
| Custo de licença/hospedagem (3) | 5 | 5 | 2 |
| **Total ponderado** | **50** | 42 | 35 |

## Mais Informações
Microsoft.EntityFrameworkCore 10.0.13 e Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 (NuGet), Aspire.Hosting.PostgreSQL 13.6.1 — verificados em 2026-10-13. Versão do PostgreSQL oferecida pelo DigitalOcean gerenciado: não verificado — confirmar na SPEC-0005 (usar a maior versão suportada ≥ 17). Referência: padrão expand/contract.

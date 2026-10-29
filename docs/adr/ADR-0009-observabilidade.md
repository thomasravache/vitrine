---
id: ADR-0009
title: Observabilidade — OpenTelemetry com Grafana Cloud e Sentry
status: accepted
origin: decision
date: 2026-10-13
pillars: [observabilidade]
baseline: observabilidade@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de integração do health check + teste de mascaramento de logs (SPEC-0007)
---

# ADR-0009 — Observabilidade — OpenTelemetry com Grafana Cloud e Sentry

## Contexto e Problema
Sem sinais e metas definidos, problemas em produção são descobertos pelo usuário e o rollback vira decisão no escuro. No Vitrine, a falha mais grave é silenciosa: uma pergunta que não recebe resposta em 2 minutos, ou um webhook do Mercado Livre que não chega. O Aspire já instrumenta tudo com OpenTelemetry via ServiceDefaults; falta para onde mandar em produção (o dashboard do Aspire não guarda histórico nem alerta).

## Direcionadores da Decisão
- OpenTelemetry nativo do Aspire (sem lock-in de instrumentação).
- VPS pequena: não queremos hospedar uma stack de observabilidade.
- Custo zero ou perto de zero no lançamento.

## Opções Consideradas
- OTLP → Grafana Cloud (plano gratuito: logs, métricas, traces, alertas, verificação sintética) + Sentry (erros do backend e da SPA)
- Stack auto-hospedada (SigNoz ou Grafana LGTM) no droplet
- Aspire Dashboard standalone em produção

## Resultado da Decisão
**Opção escolhida:** "OTLP → Grafana Cloud + Sentry", porque o plano gratuito cobre o volume do lançamento, tem alertas e uptime, e não consome a VPS.

**Regras (verificáveis):**
- Logs estruturados (JSON) com ID de correlação (trace id do OpenTelemetry) que atravessa HTTP, handlers Wolverine e chamadas ao ML/IA.
- Métricas de requisição (taxa, erro, duração) e de recurso para o host da API; métricas de negócio: perguntas recebidas, respondidas, tempo pergunta→resposta, webhooks recebidos/falhos, sugestões aprovadas sem edição.
- Traces distribuídos de ponta a ponta (webhook → fila → IA → envio ao ML).
- Health checks `/health/live` e `/health/ready` (inclui banco).
- SLI e SLO para as jornadas críticas, com orçamento de erro: disponibilidade da aplicação ≥ 99,5% mensal; respostas automáticas enviadas em ≤ 2 min em ≥ 99% dos casos elegíveis (SLO definido na spec de respostas).
- Alertas disparam por SLO e por saturação (disco > 80%, memória > 90%); cada alerta tem responsável (thomas) e runbook curto em `docs/runbooks/`.
- Cada Plano de Rollout define o limite numérico que dispara rollback durante a observação do deploy.
- Nenhum dado pessoal ou segredo em logs, métricas ou traces (ADR-0006).

### Consequências
- **Boa**, porque visibilidade e alertas desde o primeiro deploy, sem infraestrutura extra.
- **Ruim**, porque o plano gratuito retém dados por 14 dias e tem limites (10 mil séries, 50 GB/sinal); estourar exige o plano Pro (US$ 19/mês + uso).

### Confirmação (G3)
Instrumentação via Aspire ServiceDefaults (OpenTelemetry); teste de integração do health check; teste de mascaramento; limite de rollback verificado no G6.

## Prós e Contras das Opções
| Critério (peso) | Grafana Cloud + Sentry | Auto-hospedado | Aspire Dashboard |
|---|---|---|---|
| Custo (3) | 5 | 3 (consome VPS) | 5 |
| Alertas e retenção (5) | 4 | 5 | 1 |
| Operação (4) | 5 | 2 | 4 |
| **Total ponderado** | **55** | 42 | 36 |

## Mais Informações
Limites do plano gratuito do Grafana Cloud (fontes secundárias, página oficial não consultada): 10 mil séries de métricas, 50 GB de logs/traces, retenção de 14 dias, 3 usuários; Pro US$ 19/mês + uso — [last9](https://last9.io/blog/grafana-cloud-pricing/), verificado em 2026-10-13. Plano gratuito do Sentry (Developer), conferido na página oficial em 2026-10-28: 1 usuário, 5 mil erros, 5 milhões de spans, 50 replays, 30 dias de retenção — [sentry.io/pricing](https://sentry.io/pricing/). Bibliotecas e imagens *(SPEC-0007, 2026-10-28, aprovadas por thomas no piloto automático; versões com pelo menos duas semanas)*: Sentry.AspNetCore 6.12.1 e @sentry/react 11.7.0 (MIT), grafana/alloy v1.20.1 (Apache-2.0), Npgsql.OpenTelemetry 10.0.3 (PostgreSQL License) para spans de banco, OpenTelemetry.Exporter.InMemory 1.19.1 (Apache-2.0, só testes). Referências: OpenTelemetry; Google SRE (SLI, SLO).

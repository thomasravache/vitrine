---
id: ADR-0010
title: Resiliência e recuperação
status: accepted
origin: decision
date: 2026-10-13
pillars: [resiliencia]
baseline: resiliencia@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de restauração registrado (SPEC-0005) + testes de integração de timeout/idempotência
---

# ADR-0010 — Resiliência e recuperação

## Contexto e Problema
Backup que nunca foi restaurado não é backup, e falhas de dependências externas derrubam o sistema inteiro quando não há limites. O Vitrine depende de duas APIs externas (Mercado Livre e Anthropic) e recebe webhooks; o Mercado Livre não aceita duas respostas para a mesma pergunta, então repetir uma operação pode causar erro visível ao comprador.

## Direcionadores da Decisão
- PostgreSQL gerenciado com backup e restauração pontual (ADR-0004).
- Processamento assíncrono durável (ADR-0014).
- Single droplet em produção no lançamento.

## Opções Consideradas
- Backup do PostgreSQL gerenciado (PITR) + `Microsoft.Extensions.Http.Resilience` (timeouts, retry com backoff, circuit breaker) + idempotência por chave de negócio + reconciliação periódica com a API do ML
- Backup próprio (pg_dump agendado) + Polly configurado manualmente

## Resultado da Decisão
**Opção escolhida:** "PITR gerenciado + Http.Resilience + idempotência + reconciliação", porque usa o padrão do .NET/Aspire e cobre a perda de webhooks.

**Regras (verificáveis):**
- Backup automático dos dados (PITR do banco gerenciado, 7 dias) com restauração testada a cada trimestre e na SPEC-0005.
- RPO ≤ 15 minutos; RTO ≤ 4 horas (recriar droplet por IaC + restaurar banco + redeploy da última tag).
- Chamadas externas (ML, Anthropic, e-mail) com timeout, retry com backoff exponencial e jitter, e circuit breaker; nunca espera indefinida.
- Operações repetíveis são idempotentes: webhook do ML deduplicado pelo par (tópico, recurso, tentativa); envio de resposta registra o ID da pergunta antes de enviar e nunca envia duas vezes; aplicar sugestão a anúncio usa versão esperada.
- Webhooks perdidos são recuperados por reconciliação periódica (consulta de perguntas não respondidas e notificações perdidas na API do ML).
- Falha de dependência não crítica degrada a funcionalidade em vez de derrubar o sistema (ex.: IA fora → pergunta vai para aprovação manual e o vendedor é avisado).

### Consequências
- **Boa**, porque webhook perdido ou IA indisponível não vira pergunta sem resposta em silêncio.
- **Ruim**, porque sem alta disponibilidade uma queda do droplet para o serviço até a recuperação (RTO 4 h); aceito no lançamento, revisto ao atingir 30 clientes.

### Confirmação (G3)
Teste de restauração registrado na SPEC-0005; testes de integração para timeout, retry e idempotência nas specs que chamam serviços externos.

## Prós e Contras das Opções
| Critério (peso) | PITR + Http.Resilience | pg_dump + Polly manual |
|---|---|---|
| RPO (5) | 5 | 2 |
| Esforço (3) | 5 | 3 |
| **Total ponderado** | **40** | 19 |

## Mais Informações
Microsoft.Extensions.Http.Resilience 10.10.0 (NuGet, 2026-09-09), verificado em 2026-10-13. Retenção de PITR do PostgreSQL gerenciado do DigitalOcean (7 dias): não verificado — confirmar na SPEC-0005. Referências: Google SRE; Well-Architected (confiabilidade).

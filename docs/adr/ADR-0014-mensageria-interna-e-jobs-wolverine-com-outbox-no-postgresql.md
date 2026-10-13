---
id: ADR-0014
title: "Mensageria interna e jobs: Wolverine com outbox no PostgreSQL"
status: proposed
origin: decision
date: 2026-10-13
pillars: []
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de arquitetura (handlers só no próprio módulo) + teste de integração de durabilidade da fila (SPEC-0003)
---

# ADR-0014 — Mensageria interna e jobs: Wolverine com outbox no PostgreSQL

## Contexto e Problema
O Vitrine precisa de processamento assíncrono confiável: webhook do Mercado Livre deve ser aceito em milissegundos e processado depois; a resposta automática precisa sair em até 2 minutos mesmo com retries; módulos se comunicam por eventos de integração (ADR-0001); tarefas periódicas (reconciliação, renovação de tokens, coleta de métricas de visitas). No estilo de Milan Jovanović, isso era feito com MediatR + MassTransit, mas ambos passaram a ter licença comercial em 2025.

## Direcionadores da Decisão
- Sem broker extra na VPS (usar o PostgreSQL que já existe).
- Outbox transacional (evento só sai se a transação de negócio confirmar).
- Licença livre para uso comercial.
- Tarefas agendadas e retries com backoff.

## Opções Consideradas
- Wolverine (WolverineFx) com persistência e fila duráveis no PostgreSQL (outbox/inbox), handlers locais e mensagens agendadas
- MassTransit v9 + MediatR (licença comercial)
- Implementação própria: tabela de outbox + `BackgroundService` + Quartz.NET

## Resultado da Decisão
**Opção escolhida:** "Wolverine com PostgreSQL", porque entrega mediator, filas duráveis, outbox/inbox, retries e agendamento em uma biblioteca MIT, usando o banco existente.

**Regras:**
- Webhooks do ML são persistidos (inbox) e respondidos com 200 imediatamente; o processamento acontece em handler durável.
- Eventos de integração entre módulos são publicados pelo outbox, na mesma transação da mudança de estado.
- Handlers de um módulo só consomem eventos do contrato público de outros módulos (`IntegrationEvents`).
- Retries com backoff e fila de mensagens mortas monitorada (alerta quando > 0).
- Mensagens com prazo (resposta automática) carregam o instante de chegada da pergunta para medir o SLO.

### Consequências
- **Boa**, porque não há RabbitMQ/Redis para operar, e a durabilidade vem do mesmo banco com backup.
- **Ruim**, porque o PostgreSQL vira também fila; em volume alto pode exigir broker dedicado (Wolverine suporta RabbitMQ sem trocar handlers).
- **Ruim**, porque Wolverine tem convenções próprias (descoberta de handlers por convenção) que o time precisa aprender.

### Confirmação (G3)
Teste de arquitetura: handlers e assinaturas de eventos respeitam fronteiras de módulo; teste de integração: mensagem persistida sobrevive a reinício do host e é processada (SPEC-0003).

## Prós e Contras das Opções
| Critério (peso) | Wolverine + PG | MassTransit + MediatR | Própria + Quartz |
|---|---|---|---|
| Licença livre (5) | 5 | 1 | 5 |
| Outbox/inbox/retry prontos (4) | 5 | 5 | 2 |
| Sem infraestrutura extra (4) | 5 | 3 (precisa de transporte) | 5 |
| Curva de aprendizado (2) | 3 | 4 | 4 |
| **Total ponderado** | **71** | 45 | 61 |

## Mais Informações
WolverineFx 6.49.0, licença MIT (catálogo NuGet de 2026-10-12), verificado em 2026-10-13. Contexto de licenças: [Milan Jovanović — MediatR and MassTransit going commercial](https://www.milanjovanovic.tech/blog/mediatr-and-masstransit-going-commercial-what-this-means-for-you). Termos atuais da licença do MassTransit v9: não verificados na fonte oficial (fontes secundárias divergem).

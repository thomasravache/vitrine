---
id: ADR-0012
title: Custo — modelo de custo de infraestrutura e IA
status: proposed
origin: decision
date: 2026-10-13
pillars: [custo]
baseline: custo@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: N/A — revisão mensal registrada em docs/runbooks/custo.md e limites de gasto nos provedores
---

# ADR-0012 — Custo — modelo de custo de infraestrutura e IA

## Contexto e Problema
Custo descoberto só na fatura inviabiliza produtos que funcionam. No Vitrine o custo que mais escala **não é a infraestrutura, é a IA**: cada pergunta respondida e cada anúncio melhorado é uma chamada paga, e a receita é fixa por plano (R$ 49–199/mês). Orçamento informado pelo usuário: sem teto rígido.

## Direcionadores da Decisão
- Hospedagem em VPS (ADR-0004), modelos Claude (ADR-0015).
- Preço da assinatura R$ 49–199 por quantidade de anúncios (a validar).
- Meta: 30 clientes pagantes em 3 meses.

## Opções Consideradas
- Acompanhar custo por provedor com limites de gasto + métrica interna de custo de IA por workspace (tokens × preço) desde o primeiro dia
- Acompanhar só pela fatura mensal

## Resultado da Decisão
**Opção escolhida:** "Limites nos provedores + custo de IA por workspace medido pela aplicação", porque a margem por cliente depende do volume de perguntas, que varia muito entre vendedores.

**Premissas (a validar com dados reais):** câmbio R$ 5,40/US$ (não verificado); por cliente: 1.000 perguntas/mês e 300 anúncios; resposta ≈ 3.000 tokens de entrada + 300 de saída no Sonnet 5.5; melhoria de anúncio ≈ 5.000 de entrada + 2.000 de saída no Opus 5.5; 10% dos anúncios melhorados por mês após a carga inicial; 3 testes grátis por cliente pagante, com teto de 20 melhorias por teste.

**Estimativa mensal:**
| Item | Atual (30 clientes) | 2x (60) | 10x (300) |
|---|---|---|---|
| Droplet produção | US$ 24 | US$ 48 | US$ 96 (+ LB e 2º droplet) |
| PostgreSQL gerenciado | US$ 15 | US$ 30 | US$ 60–120 (HA) |
| Droplet staging | US$ 12 | US$ 12 | US$ 24 |
| Spaces (artefatos/backups extras) | US$ 5 | US$ 5 | US$ 5 |
| Observabilidade (Grafana/Sentry) | US$ 0 | US$ 0–19 | US$ 50–100 |
| **Infraestrutura** | **~US$ 56** | **~US$ 95–114** | **~US$ 235–345** |
| IA — respostas (US$ 0,008 cada) | US$ 240 | US$ 480 | US$ 2.400 |
| IA — melhorias recorrentes (US$ 0,06 cada) | US$ 54 | US$ 108 | US$ 540 |
| IA — testes grátis | US$ 108 | US$ 216 | US$ 1.080 |
| **IA** | **~US$ 400** | **~US$ 800** | **~US$ 4.000** |
| Carga inicial de anúncios (uma vez por cliente) | US$ 18 por cliente | — | — |
| Receita estimada (ticket médio R$ 120) | ~US$ 667 | ~US$ 1.333 | ~US$ 6.667 |

**Leitura:** com essas premissas, IA consome ~60% da receita. Antes de fixar os preços, a spec de planos deve: (1) limitar respostas e melhorias por plano (como o SelerAI, que limita 500 respostas no plano de R$ 97); (2) medir o custo real por resposta com cache de prompt, que pode reduzir a entrada repetida; (3) avaliar Haiku 5.5 para perguntas simples se a avaliação de qualidade permitir.

**Regras (verificáveis):**
- Estimativa mensal antes de escolher hospedagem e serviços, com premissas de uso explícitas e cenários (atual, 2x, 10x) — esta tabela.
- Preços verificados na fonte oficial com a data da verificação; se não verificados, isso é declarado.
- A estimativa é comparada com o orçamento informado; estourar exige decisão humana.
- Alerta de gasto: DigitalOcean em US$ 100/mês; limite de gasto mensal na conta da Anthropic em US$ 500 (ajustado conforme clientes); revisão de custo real mensal registrada em `docs/runbooks/custo.md`.
- Recursos identificados por ambiente e serviço (tags `env:staging|production`, `service:vitrine`); custo de IA registrado por workspace e por funcionalidade.

### Consequências
- **Boa**, porque a margem por cliente fica visível desde o primeiro cliente e orienta o preço dos planos.
- **Ruim**, porque medir custo por workspace exige registrar uso de tokens em cada chamada (pequeno esforço nas specs de IA).

### Confirmação (G3)
Estimativa neste ADR; alertas nos painéis do DigitalOcean e da Anthropic configurados na SPEC-0005; revisão mensal registrada.

## Prós e Contras das Opções
| Critério (peso) | Limites + métrica por workspace | Só fatura |
|---|---|---|
| Visibilidade de margem (5) | 5 | 1 |
| Esforço (2) | 3 | 5 |
| **Total ponderado** | **31** | 15 |

## Mais Informações
- Preços Claude por milhão de tokens (entrada/saída): Opus 5.5 US$ 4/20; Sonnet 5.5 US$ 2/10 (leitura de cache US$ 0,20); Haiku 5.5 US$ 0,10/0,50 — referência de modelos da Anthropic com cache de 2026-10-06, consultada em 2026-10-13.
- DigitalOcean: fontes secundárias (droplet 4 GB US$ 24; Postgres desde US$ 15), não verificado na página oficial — [infratally](https://infratally.com/articles/digitalocean-droplet-pricing-guide-2026/).
- Concorrente: SelerAI Starter R$ 97/mês, 1 loja, 500 respostas de IA — [selerai.com.br](https://www.selerai.com.br/), verificado em 2026-10-13.
- Referência: FinOps Foundation.

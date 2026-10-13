---
id: ADR-0015
title: "Provedor de IA: Claude (Opus 5.5 e Sonnet 5.5)"
status: proposed
origin: user
date: 2026-10-13
pillars: []
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de arquitetura (só o adaptador de IA referencia o SDK Anthropic) + suíte de avaliação versionada
---

# ADR-0015 — Provedor de IA: Claude (Opus 5.5 e Sonnet 5.5)

## Contexto e Problema
As duas funções centrais usam IA: reescrever título, descrição e ficha técnica de anúncios, e responder perguntas de compradores sem inventar informação. O usuário escolheu a recomendação: Claude Opus 5.5 para melhorar anúncios (volume baixo, valor alto) e Claude Sonnet 5.5 para respostas (volume alto, latência e custo importam).

## Direcionadores da Decisão
- Qualidade de escrita em português do Brasil.
- Regra inegociável: a resposta nunca inventa informação que não está no anúncio; na dúvida, vai para o vendedor.
- Custo por resposta compatível com planos de R$ 49–199 (ADR-0012).
- Resposta em até 2 minutos.

## Opções Consideradas
- Opus 5.5 (anúncios) + Sonnet 5.5 (respostas) — escolha do usuário
- Sonnet 5.5 para tudo
- Opus 5.5 + Haiku 5.5

## Resultado da Decisão
**Opção escolhida:** "Opus 5.5 + Sonnet 5.5" (origin: user). Integração pelo SDK oficial da Anthropic para C# (pacote NuGet `Anthropic`), isolada atrás de uma porta (`IAnswerGenerator`, `IListingImprover`) no módulo correspondente, para permitir trocar modelo por configuração.

**Regras:**
- Saídas estruturadas (`output_config.format` com JSON Schema): a resposta traz `answer`, `grounded` (bool), `sources` (trechos do anúncio usados) e `escalate_reason`; resposta sem fonte no anúncio é escalada ao vendedor, nunca enviada automaticamente.
- Tratar `stop_reason: "refusal"` e indisponibilidade: pergunta vai para aprovação manual (ADR-0010).
- Modelo e esforço (`effort`) configuráveis por funcionalidade; IDs exatos `claude-opus-5-5` e `claude-sonnet-5-5`.
- Cache de prompt para o prefixo estável (instruções + dados do anúncio).
- Registrar tokens de entrada/saída por chamada e workspace (ADR-0012).
- Mudança de modelo ou prompt só com a suíte de avaliação (ADR-0002) executada e resultado anexado ao PR.

### Consequências
- **Boa**, porque usa o modelo mais capaz onde o volume é baixo e um modelo rápido e mais barato no caminho de alto volume.
- **Ruim**, porque dependemos de um provedor externo (latência, disponibilidade, preço); a porta de abstração e a degradação para modo manual mitigam.
- **Ruim**, porque o custo de IA é o maior custo variável do produto (ADR-0012).

### Confirmação (G3)
Teste de arquitetura: apenas `*.Infrastructure` dos módulos Listings/Questions referencia o pacote `Anthropic`; suíte de avaliação nas specs de IA.

## Prós e Contras das Opções
| Critério (peso) | Opus + Sonnet | Sonnet só | Opus + Haiku |
|---|---|---|---|
| Qualidade da reescrita (4) | 5 | 4 | 5 |
| Respeitar "não inventar" nas respostas (5) | 4 | 4 | 3 |
| Custo por resposta (4) | 3 | 3 | 5 |
| **Total ponderado** | **52** | 48 | 55 (risco maior na regra principal) |

## Mais Informações
Modelos e preços (referência da Anthropic com cache de 2026-10-06, consultada em 2026-10-13): Opus 5.5 US$ 4/20 por milhão de tokens; Sonnet 5.5 US$ 2/10; Haiku 5.5 US$ 0,10/0,50. SDK C# `Anthropic` 12.55.0 (NuGet), verificado em 2026-10-13. A matriz favorece Opus + Haiku em custo; a escolha de Sonnet prioriza a regra inegociável e pode ser revista com a suíte de avaliação.

---
id: ADR-0013
title: "Frontend: SPA React com Vite"
status: accepted
origin: decision
date: 2026-10-13
pillars: []
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: drift do cliente OpenAPI verificado no CI + E2E Playwright em viewport móvel
---

# ADR-0013 — Frontend: SPA React com Vite

## Contexto e Problema
O vendedor usa o Vitrine principalmente pelo celular (não haverá app nas lojas). Todas as telas ficam atrás de login, então não há necessidade de SEO nem de renderização no servidor. O frontend precisa rodar sob o Aspire (ADR-0001) e falar com a API .NET por um contrato tipado.

## Direcionadores da Decisão
- Site responsivo, mobile-first.
- Integração de primeira classe com Aspire (`AddViteApp`).
- Contrato tipado de ponta a ponta entre C# e TypeScript.
- Mesma origem da API para autenticação por cookie (ADR-0007).

## Opções Consideradas
- React 19 + Vite 8 + TypeScript + TanStack Router/Query + Tailwind CSS + componentes shadcn/ui; cliente da API gerado do OpenAPI do ASP.NET Core
- Next.js (SSR) como serviço Node separado
- Blazor WebAssembly

## Resultado da Decisão
**Opção escolhida:** "React + Vite SPA", porque é o maior ecossistema de UI, o Aspire tem suporte nativo a Vite, o build gera arquivos estáticos servidos pelo próprio host da API (mesma origem, sem servidor Node em produção) e SSR não traz ganho para telas autenticadas.

**Regras:**
- A SPA fica em `web/`; em desenvolvimento roda via `AddViteApp` no AppHost; em produção é compilada e servida como arquivos estáticos pelo host `Vitrine.Api`.
- O cliente HTTP TypeScript é gerado a partir do documento OpenAPI da API (`Microsoft.AspNetCore.OpenApi`); o CI falha se o cliente versionado divergir do OpenAPI atual.
- Mobile-first: toda jornada E2E roda também em viewport de celular (Playwright, perfil de dispositivo móvel).
- Nenhum token ou segredo no código da SPA; autenticação apenas por cookie HttpOnly.
- Ferramentas do cliente e da SPA *(adicionadas em 2026-10-14, aprovação pendente)*: geração do documento OpenAPI no build com Microsoft.Extensions.ApiDescription.Server 10.0.13 (MIT); cliente TypeScript gerado por **orval 8.37.0** (MIT; openapi-typescript e @hey-api/openapi-ts não funcionam com TypeScript 7); fontes Inter e Plus Jakarta Sans auto-hospedadas via @fontsource-variable 5.3.0 (OFL-1.1), sem Google Fonts (compatível com a CSP da SPEC-0008).

### Consequências
- **Boa**, porque produção tem um único container e uma única origem; desenvolvimento tem HMR sob o Aspire.
- **Ruim**, porque duas linguagens; mitigado pelo cliente gerado do OpenAPI.

### Confirmação (G3)
Verificação de drift do cliente OpenAPI no CI; E2E em viewport móvel; Biome/tsc (ADR-0003).

## Prós e Contras das Opções
| Critério (peso) | React + Vite | Next.js | Blazor WASM |
|---|---|---|---|
| Ecossistema de UI e mercado (4) | 5 | 5 | 2 |
| Simplicidade em produção (sem Node) (4) | 5 | 2 | 5 |
| Integração Aspire (3) | 5 | 4 | 5 |
| Desempenho no celular (3) | 4 | 5 | 2 |
| **Total ponderado** | **67** | 54 | 49 |

## Mais Informações
Versões estáveis no npm, verificadas em 2026-10-13: react 19.3.0; vite 8.3.4; typescript 7.0.2; @tanstack/react-router 1.170.44; @tanstack/react-query 5.104.2. Aspire.Hosting.JavaScript 13.6.1 — [Aspire para desenvolvedores JavaScript](https://devblogs.microsoft.com/aspire/aspire-for-javascript-developers). Versões de Tailwind CSS e shadcn/ui: confirmar na SPEC-0002.

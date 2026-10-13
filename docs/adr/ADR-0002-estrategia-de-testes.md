---
id: ADR-0002
title: Estratégia de testes
status: accepted
origin: decision
date: 2026-10-13
pillars: [testes]
baseline: testes@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: comandos test, test_integration, test_e2e e arch_test do sdd-config no CI
---

# ADR-0002 — Estratégia de testes

## Contexto e Problema
Sem estratégia explícita, os testes ficam desbalanceados (muitos E2E instáveis ou nenhum teste de integração real) e o CI deixa de ser confiável. No Vitrine, os erros mais caros são alterar um anúncio sem aprovação, responder com informação inventada e perder o prazo de 2 minutos — todos precisam de prova automática. A API do Mercado Livre e a API da Anthropic são externas e não podem ser chamadas de verdade no CI.

## Direcionadores da Decisão
- Stack .NET 10 + React/TS (ADR-0001, ADR-0013).
- Integração com dependências reais (PostgreSQL em container), não mocks.
- Serviços externos (Mercado Livre, Anthropic) substituídos por servidores falsos HTTP com contrato verificado.
- Avaliação de qualidade da IA (respostas não inventam dados) como nível próprio, separado da suíte determinística.

## Opções Consideradas
- xUnit v3 + Testcontainers + Aspire.Hosting.Testing + Playwright (TS) + Vitest
- NUnit + banco em memória (EF InMemory/SQLite) + Cypress

## Resultado da Decisão
**Opção escolhida:** "xUnit v3 + Testcontainers + Aspire.Hosting.Testing + Playwright + Vitest", porque testa contra PostgreSQL real, sobe a aplicação inteira pelo próprio AppHost no E2E e usa ferramentas mantidas ativamente.

**Regras (verificáveis):**
- Níveis e obrigatoriedade conforme a skill: unitário, integração com dependências reais, contrato entre specs e E2E das jornadas de usuário.
- A suíte completa roda no CI em toda solicitação de mudança.
- As jornadas críticas estão em `critical_journeys` no sdd-config e cada uma tem ao menos um E2E.
- Teste instável é defeito: vai para quarentena por no máximo 7 dias com spec aberta para corrigir; nunca retry-até-passar.
- Cobertura das linhas alteradas ≥ 80% quando a medição estiver configurada.
- Chamadas ao Mercado Livre e à Anthropic nos testes passam por servidores falsos (WireMock.Net) cujos cenários seguem a documentação oficial; nenhum teste do CI chama serviços externos reais.
- Funcionalidades de IA têm uma suíte de avaliação (eval) versionada com casos reais anonimizados; ela roda manualmente/por agendamento (custa dinheiro) e seu resultado é evidência no G4 das specs de IA.

**Ferramenta por nível:**
| Nível | Ferramenta / abordagem | Por quê | Onde roda |
|---|---|---|---|
| Unitário | xUnit v3 (C#); Vitest (SPA) | padrão do ecossistema, rápido, paralelo | local + CI |
| Integração | xUnit v3 + Testcontainers.PostgreSql (Postgres real em container) + WireMock.Net para ML/Anthropic + `WebApplicationFactory` | banco real, sem mocks de persistência; externos com contrato controlado | local + CI |
| Contrato | Testes de contrato dos eventos de integração entre módulos (schema do evento publicado vs. consumidor) e do cliente TS gerado do OpenAPI (verificação de drift no CI) | módulos e SPA consomem contratos de outras specs | CI |
| E2E | Playwright (@playwright/test, TypeScript) contra a aplicação subida por `Aspire.Hosting.Testing`/AppHost | jornada real no navegador, mesmo AppHost do dev | local + CI + staging (smoke) |
| Cobertura | coverlet.MTP 10.1.0 (coletor para Microsoft Testing Platform, MIT) + ReportGenerator 5.5.11 (Apache-2.0) + diff-cover 10.6.0 (linhas alteradas ≥ 80%, Apache-2.0, executado via uv) | `--collect` não existe no MTP; diff-cover é o padrão de mercado para cobertura de diff *(adicionado em 2026-10-13, aprovado por thomas)** | local (sem diff-cover se não houver uv) + CI |
| Carga | k6 apenas para o caminho "pergunta → resposta automática" (SLA 2 min) quando a spec de respostas existir | único RNF de desempenho do lançamento | staging |
| Acessibilidade | @axe-core/playwright nas telas principais (sem violações sérias/críticas) | site deve funcionar bem no celular e para todos | CI |

### Consequências
- **Boa**, porque os testes de integração pegam problemas reais de SQL/migração e o E2E usa o mesmo AppHost do desenvolvimento.
- **Ruim**, porque Testcontainers exige Docker no CI e local (ambos disponíveis), e a suíte de integração é mais lenta que mocks.
- **Ruim**, porque servidores falsos do ML podem divergir da API real; mitigação: smoke em staging com conta de teste real do ML.

### Confirmação (G3)
Comandos `test`, `test_integration`, `test_e2e` e `coverage` do sdd-config, executados pelo `gate --run` e pelo CI.

## Prós e Contras das Opções
| Critério (peso) | xUnit v3 + Testcontainers + Playwright | NUnit + InMemory + Cypress |
|---|---|---|
| Fidelidade (banco real) (5) | 5 | 2 |
| Integração com Aspire (4) | 5 | 3 |
| Manutenção ativa e ecossistema (3) | 5 | 4 |
| Velocidade da suíte (2) | 3 | 5 |
| **Total ponderado** | **76** | 48 |

## Mais Informações
Versões verificadas em 2026-10-13 (NuGet/npm): xunit.v3 4.0.2; Testcontainers.PostgreSql 4.16.0; Aspire.Hosting.Testing 13.6.1; WireMock.Net 2.19.0; @playwright/test 1.64.0; vitest 5.0.4; @axe-core/playwright 4.13.0. Referências: pirâmide de testes; references/testing.md da skill.
Fontes oficiais de versão consultadas por API em 2026-10-13: [nuget.org/packages/xunit.v3](https://www.nuget.org/packages/xunit.v3), [nuget.org/packages/Testcontainers.PostgreSql](https://www.nuget.org/packages/Testcontainers.PostgreSql), [npmjs.com/package/@playwright/test](https://www.npmjs.com/package/@playwright/test).

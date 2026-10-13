---
id: ADR-0001
title: Arquitetura e fronteiras — monólito modular .NET 10 orquestrado por Aspire
status: proposed
origin: user
date: 2026-10-13
pillars: [arquitetura]
baseline: arquitetura@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: tests/Vitrine.ArchitectureTests (ArchUnitNET) no CI
---

# ADR-0001 — Arquitetura e fronteiras — monólito modular .NET 10 orquestrado por Aspire

## Contexto e Problema
Vitrine é um SaaS B2B para vendedores do Mercado Livre (20 a 2.000 anúncios, uma ou mais contas ML) com dois núcleos — melhoria de anúncios e respostas a perguntas — mais histórico/rollback e assinatura. Time pequeno, produto novo, carga modesta no lançamento (meta: 30 clientes pagantes em 3 meses) e um requisito de latência forte: resposta automática em até 2 minutos. Sem fronteiras explícitas e verificadas, módulos se acoplam aos poucos e cada mudança passa a exigir mexer em tudo.

## Direcionadores da Decisão
- Escolha do usuário (origin: user): monólito modular no estilo de Milan Jovanović, com .NET Aspire.
- Time pequeno: um único implantável, um banco, operação simples em VPS (ADR-0004).
- Domínios bem separados (Contas ML, Anúncios, Perguntas, Histórico, Assinaturas) que podem evoluir em ritmos diferentes.
- Integração assíncrona com o Mercado Livre (notificações por webhook) e com IA — precisa de processamento em segundo plano confiável (ADR-0014).

## Opções Consideradas
- Monólito modular .NET 10 (LTS) + Aspire 13 — escolha do usuário
- Monólito em camadas único (sem módulos)
- Microsserviços

## Resultado da Decisão
**Opção escolhida:** "Monólito modular .NET 10 + Aspire 13", escolha do usuário, coerente com os direcionadores: um implantável, fronteiras de módulo verificadas por teste, extração futura barata.

**Desenho:**
- `src/AppHost` (Aspire): orquestra Postgres, o host `Vitrine.Api` e a SPA `web/` (ADR-0013) no desenvolvimento e gera os artefatos de deploy (ADR-0004).
- `src/ServiceDefaults`: OpenTelemetry, health checks, resiliência HTTP (padrão Aspire).
- `src/Api` (host): compõe os módulos; serve a SPA na mesma origem; executa os handlers Wolverine (ADR-0014).
- `src/Modules/<Modulo>/` com projetos `Domain`, `Application`, `Infrastructure`, `Presentation` e `IntegrationEvents` (contrato público). Módulos iniciais previstos: `Identity` (usuários e workspaces), `MercadoLivre` (contas conectadas, tokens, gateway da API ML), `Listings` (anúncios e sugestões), `Questions` (perguntas e respostas), `History` (histórico e rollback), `Billing` (teste grátis e assinatura). A fundação cria apenas `Identity`; os demais nascem nas specs dos épicos de produto.
- `src/Common/*` (building blocks): abstrações compartilhadas sem regra de negócio.
- Cada módulo tem seu schema no PostgreSQL (`identity`, `listings`, …) e seu `DbContext`.

**Regras (verificáveis):**
- Cada módulo expõe um contrato público (`<Modulo>.IntegrationEvents` e, se preciso, `<Modulo>.PublicApi`); os demais só dependem desse contrato.
- Dependências proibidas: `Domain` não referencia `Application`, `Infrastructure` nem `Presentation`; `Application` não referencia `Infrastructure` nem `Presentation`; nenhum projeto de um módulo referencia projetos internos (`Domain`/`Application`/`Infrastructure`/`Presentation`) de outro módulo; `Common` não referencia módulos.
- Não há ciclos de dependência entre módulos.
- Um módulo não lê nem escreve tabelas de outro schema.
- Comunicação entre módulos por eventos de integração (assíncrona, via outbox — ADR-0014) ou pela `PublicApi` (síncrona, só leitura).
- Mudar uma fronteira exige um ADR novo (que substitui este).

### Consequências
- **Boa**, porque um único deploy e um único banco mantêm a operação barata, e as fronteiras verificadas permitem extrair um módulo (ex.: Perguntas, que tem SLA próprio) para um processo separado sem reescrever.
- **Boa**, porque Aspire dá orquestração local com um comando, dashboard de telemetria e geração do Docker Compose de deploy.
- **Ruim**, porque duas linguagens (C# no backend, TypeScript na SPA) exigem contrato de API tipado gerado (OpenAPI → cliente TS) para não divergir.
- **Ruim**, porque o padrão de módulos com 4–5 projetos cada adiciona cerimônia; aceito pelo ganho de fronteiras verificáveis.

### Confirmação (G3)
Testes de arquitetura com ArchUnitNET (`TngTech.ArchUnitNET.xUnitV3`) em `tests/Vitrine.ArchitectureTests`, executados no CI (`arch_test` do sdd-config). Uma regra por item da lista "Dependências proibidas" + regra de ausência de ciclos; um teste de schema verifica que cada `DbContext` só mapeia o próprio schema.

## Prós e Contras das Opções
| Critério (peso) | Monólito modular .NET + Aspire | Monólito em camadas | Microsserviços |
|---|---|---|---|
| Escolha/fluência do usuário (5) | 5 | 3 | 2 |
| Custo e simplicidade de operação em VPS (5) | 5 | 5 | 1 |
| Fronteiras verificáveis e evolução (4) | 5 | 2 | 5 |
| Velocidade de entrega inicial (4) | 4 | 5 | 1 |
| Escala independente (Perguntas com SLA) (2) | 3 (extração possível) | 1 | 5 |
| **Total ponderado** | **94** | 73 | 52 |

## Mais Informações
- .NET 10 é LTS com suporte até novembro de 2028; .NET 11 (STS) sai em novembro de 2026 — [InfoWorld](https://www.infoworld.com/article/4191704/end-of-support-looms-for-net-8-and-net-9.html), verificado em 2026-10-13. SDK local: 10.0.401.
- Aspire 13: JavaScript e Python como recursos de primeira classe (`Aspire.Hosting.JavaScript`) — [What's new in Aspire 13](https://aspire.dev/whats-new/aspire-13/); versão estável no NuGet: 13.6.1 (Aspire.Hosting.*), verificado em 2026-10-13.
- ArchUnitNET para xUnit v3: `TngTech.ArchUnitNET.xUnitV3` 0.13.4 (publicado 2026-08-20). NetArchTest.Rules foi descartado: última versão (1.3.2) é de 2021-05.
- Referências: C4 model; [Milan Jovanović — MediatR e MassTransit comerciais](https://www.milanjovanovic.tech/blog/mediatr-and-masstransit-going-commercial-what-this-means-for-you) (motiva ADR-0014).

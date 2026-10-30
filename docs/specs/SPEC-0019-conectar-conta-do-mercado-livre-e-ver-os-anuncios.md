---
id: SPEC-0019
title: Conectar conta do Mercado Livre e ver os anúncios
tier: epic
type: feature
status: approved
created: 2026-10-30
depends_on: [SPEC-0006, SPEC-0008]
adrs: [ADR-0001, ADR-0006, ADR-0007, ADR-0010, ADR-0014, ADR-0016]
external: []
approved_by: thomas
approved_at: 2026-10-30
---

# SPEC-0019 — Conectar conta do Mercado Livre e ver os anúncios (Épico)

## 1. Visão
O vendedor:
- conecta uma ou mais contas do Mercado Livre ao seu workspace pelo OAuth oficial, com PKCE;
- vê os anúncios dessas contas no Vitrine (título, preço, estoque, vendidos, estado e link), no celular e no desktop;
- vê a lista atualizada sozinha, por notificação do ML ou por reconciliação periódica;
- pode desconectar quando quiser.

Esta é a primeira jornada de produto (`conectar-conta`) e a base dos próximos épicos: melhorar anúncios e responder perguntas usam a conta conectada, o gateway autenticado e os anúncios importados.

Tecnicamente, o épico entrega:
- os módulos `MercadoLivre` (contas, tokens cifrados, gateway da API) e `Listings` (anúncios);
- o webhook de notificações;
- um simulador do ML (ADR-0016), para que tudo rode e seja testado localmente, sem custo e sem conta externa.

## 2. Escopo
**Objetivos (dentro do escopo):**
- Conectar, reconectar e desconectar contas do ML por workspace, com OAuth Authorization Code + PKCE S256 + `state` validado (ADR-0007). Os tokens ficam cifrados (ADR-0006) e nunca chegam à SPA.
- Gateway autenticado da API do ML:
  - renovação do access token antes de expirar;
  - rotação do refresh token de uso único;
  - conta em "reconectar necessário" quando o ML recusa o refresh;
  - retry/backoff com `Retry-After` e circuit breaker (ADR-0010).
- Importação completa dos anúncios ao conectar, sincronização manual, reconciliação diária e atualização por notificação do tópico `items`.
- Listagem paginada de anúncios com busca e filtros (estado e conta).
- Telas "Contas" e "Anúncios" no celular e no desktop, painel com atalhos e o E2E da jornada `conectar-conta`.
- Simulador do ML para desenvolvimento, E2E e ambiente local prod-like.

**Não-objetivos (fora do escopo):**
- Alterar anúncios no ML (título, descrição, ficha técnica): épico "Melhorar anúncios". Nada aqui pede o escopo `write` além do necessário para o OAuth.
- Perguntas e respostas (tópico `questions`): épico "Responder perguntas".
- Limite de contas por plano e cobrança: épico "Assinatura".
- Convite de membros. Hoje só existe o papel `Owner` (cadastro). As regras de papel ficam prontas para `Member`.
- Detalhe do anúncio (atributos, descrição, fotos) além da miniatura. A tela de detalhe nasce no épico de melhoria.
- Revogar a autorização do lado do ML ao desconectar: o endpoint de revogação não foi verificado. Fica pendência (seção 8).
- App no DevCenter e teste com o ML real: dependem de ação do usuário (seção 8) e entram antes do go-live.

## 3. Arquitetura Alvo
**Contexto:** projeto novo com fundação pronta (SPEC-0001). A ADR-0001 prevê os módulos `MercadoLivre` (contas conectadas, tokens, gateway da API ML) e `Listings` (anúncios). O módulo de referência é `src/Modules/Identity`. Nenhum desvio de arquitetura.

```text
SPA (/contas, /contas/mercado-livre/retorno, /anuncios, /painel)
  │ cookie de sessão, antiforgery
  ▼
Api ── MercadoLivre.Presentation  (/api/v1/mercadolivre/*)
     │      └ Application: conectar (PKCE/state), desconectar, listar contas
     │      └ Infrastructure: schema "mercadolivre" (contas, tokens cifrados com IFieldProtector,
     │                         autorizações pendentes), cliente HTTP do ML + renovação de token
     │      └ PublicApi: IMercadoLivreItems (busca de IDs + /items/bulk, com o token da conta)
     │      └ IntegrationEvents: AccountConnected / AccountDisconnected /
     │                           AccountReauthorizationRequired / ItemChanged (v1)
     ├─ Listings.Presentation  (/api/v1/listings*)
     │      └ handlers Wolverine duráveis: importação, sincronização, reconciliação diária, item alterado
     │      └ schema "listings"
     └─ Identity.PublicApi (novo): IWorkspaceMembership → workspace e papel do usuário logado

Webhook ML ──POST /api/v1/mercadolivre/notifications (anônimo)──▶ inbox durável ──▶ ItemChanged ──▶ Listings
ML (real, ou simulador em dev/E2E/local) ◀── gateway (Http.Resilience)
```

Contratos entre as filhas:
- **SPEC-0020@1:** endpoints de conexão e conta, eventos `MercadoLivreAccountConnected` e `MercadoLivreAccountDisconnected`, `IWorkspaceMembership`.
- **SPEC-0021@1:** `IMercadoLivreItems` e o evento `MercadoLivreAccountReauthorizationRequired`.
- **SPEC-0022@1:** `GET /api/v1/listings`, `POST /api/v1/listings/sync` e `GET /api/v1/listings/summary`.
- **SPEC-0023@1:** evento `MercadoLivreItemChanged`.
- **SPEC-0024@1:** contrato do simulador (subconjunto documentado da API do ML, mais `/__sim/*`).

**Decisões (ADRs):**
- Aplicadas: ADR-0001 (módulos), ADR-0006 (tokens cifrados), ADR-0007 (OAuth com PKCE e `state`, workspace como inquilino), ADR-0010 (resiliência, idempotência, reconciliação), ADR-0014 (inbox/outbox, jobs duráveis).
- Nova: **ADR-0016**, simulador do ML.

**Regras de arquitetura a garantir (G3):**
- Os módulos `MercadoLivre` e `Listings` seguem as regras de camada e isolamento já testadas (`LayerRulesTests`, `ModuleIsolationRulesTests`, ampliados com os novos assemblies).
- `Listings` depende só de `MercadoLivre.PublicApi`/`IntegrationEvents` e de `Identity.PublicApi`, nunca de projetos internos de outro módulo.
- Nenhum tipo de token sai do módulo `MercadoLivre`: um teste de arquitetura reprova tipo público de `PublicApi`/`IntegrationEvents` com propriedade de token.
- O único endpoint anônimo novo é o webhook `POST /api/v1/mercadolivre/notifications`, incluído na lista `AnonymousEndpoints` do teste da SPEC-0008.
- A Api não referencia o simulador (ADR-0016).

## 4. Decomposição
| Spec | Título | Tier | Tipo | Tamanho | Depende de | Consome contrato de |
|---|---|---|---|---|---|---|
| SPEC-0020 | Conectar e desconectar conta do Mercado Livre (OAuth com PKCE) | full | feature | M | — | — |
| SPEC-0021 | Gateway autenticado da API do Mercado Livre (renovação de token e resiliência) | full | feature | M | — | SPEC-0020@1 |
| SPEC-0022 | Importação e sincronização de anúncios (módulo Listings) | full | feature | M | — | SPEC-0020@1, SPEC-0021@1, SPEC-0023@1 |
| SPEC-0023 | Notificações do Mercado Livre (webhook de anúncios) | full | feature | M | — | SPEC-0020@1 |
| SPEC-0024 | Simulador do Mercado Livre para desenvolvimento e E2E | full | feature | M | — | — |
| SPEC-0025 | Telas de contas e anúncios (jornada conectar-conta) | full | feature | M | SPEC-0024 | SPEC-0020@1, SPEC-0022@1 |

## 5. Estratégia de Entrega
- **Ambientes:**
  - desenvolvimento (`aspire run`, com simulador) e CI (E2E com simulador);
  - ambiente local prod-like `https://vitrine.localhost`, com o simulador no perfil `ml-simulator` do Compose, para o teste manual do usuário.
  - Staging e produção só existem com a SPEC-0009.
- **Entrega por onda:** cada onda entra na `main` pelo PR com CI verde e roda no ambiente local prod-like (G6). Ondas calculadas por `spec_graph.py waves`.
- **Feature flags:** nenhuma.
  - Sem `MercadoLivre:ClientId`/`ClientSecret` configurados, conectar responde `503 ML_NOT_CONFIGURED`, e a tela mostra "Conexão com o Mercado Livre indisponível no momento".
  - Assim, staging/produção sem o app no DevCenter não quebram.
- **Rollback:** revert do merge da onda.
  - As migrações dos schemas `mercadolivre` e `listings` são só de criação (expand) e não tocam em dados existentes.
  - Reverter deixa as tabelas sem uso. A remoção vira spec própria, se necessária.
- **Métricas de sucesso pós-release** (no go-live, SPEC-0009; antes disso, no dashboard do Aspire):
  - taxa de sucesso da conexão (`vitrine.mercadolivre.connections`, por resultado);
  - falhas de renovação de token;
  - latência p95 do webhook < 500 ms;
  - duração da importação;
  - todas por 2 semanas após o lançamento.

## 6. Riscos & Mitigações
- **O simulador diverge do ML real.**
  - Ele implementa só o contrato documentado, com fontes datadas (SPEC-0024).
  - Teste manual com um usuário de teste real antes do go-live (pendência, seção 8).
- **`/items?ids=` está sendo descontinuado** (prazo de migração 25/10/2026). O gateway usa `/items/bulk` desde o início. O limite de IDs por chamada não foi verificado: o gateway usa 20, o limite documentado do multiget antigo, configurável.
- **Webhook sem assinatura** (o ML não documenta autenticação).
  - O webhook não confia no payload: valida `application_id`, ignora `user_id` desconhecido e sempre relê o recurso na API com o token da conta.
  - Rate limit por IP.
  - A lista de IPs de origem fica como defesa opcional no Caddy, no go-live.
- **Refresh token de uso único com renovações concorrentes**, que invalidariam a conta. Mitigação: renovação serializada por conta, com lock no banco (SPEC-0021:IT-03).
- **Webhook acima de 500 ms**, que faz o ML desativar o tópico. Mitigação: o endpoint só grava no inbox e responde 200 (SPEC-0023:IT-05 mede).
- **Catálogo grande** (mais de 1000 anúncios): `search_type=scan` com `scroll_id` (expira em 5 min). A importação é paginada e retomável (SPEC-0022).
- **IDs do ML acima de Int32:** `long` em todo lugar (SPEC-0020/0022).

## 7. Critérios de Aceite do Épico
- [ ] O vendedor conecta uma conta do ML e vê seus anúncios no celular e no desktop — SPEC-0025:E2E-01.
- [ ] Os tokens do ML ficam cifrados no banco e nunca aparecem em resposta da API nem em log — SPEC-0020:IT-07.
- [ ] Access token expirado é renovado sem ação do vendedor; refresh recusado leva a conta a "reconectar necessário" e a tela mostra isso — SPEC-0021:IT-02, SPEC-0025:E2E-02.
- [ ] Anúncio alterado no ML aparece atualizado após a notificação, e notificação perdida é recuperada pela reconciliação — SPEC-0023:IT-01, SPEC-0022:IT-09, SPEC-0022:IT-05.
- [ ] Desconectar apaga os tokens e os anúncios da conta e some da tela — SPEC-0020:IT-05, SPEC-0022:IT-06, SPEC-0025:E2E-03.
- [ ] Um workspace não vê contas nem anúncios de outro — SPEC-0020:IT-08, SPEC-0022:IT-07.

## 8. Questões em Aberto
- [x] Quantas contas do ML por workspace? — Várias, sem limite neste épico (ADR-0007: "um cliente pode ter várias contas"). O limite por plano vem do épico de assinatura. (Architect, 2026-10-30, a confirmar no H1)
- [x] Quem pode conectar e desconectar? — Só `Owner`; qualquer membro vê contas e anúncios. Hoje só existe Owner (ADR-0007). (Architect, 2026-10-30, a confirmar no H1)
- [x] A mesma conta do ML pode estar em dois workspaces? — Não: `409 ML_ACCOUNT_IN_USE`. As notificações do ML chegam por `user_id` e precisam de um único dono. (Architect, 2026-10-30, a confirmar no H1)
- [x] O que acontece com os anúncios ao desconectar? — São apagados junto com os tokens (ADR-0006: retenção "até desconectar"). Reconectar reimporta. (Architect, 2026-10-30, a confirmar no H1)
- [x] Só contas do Brasil? — Sim, `site_id = MLB`; outra conta recebe `422 ML_SITE_NOT_SUPPORTED` (o produto é para o ML Brasil). (Architect, 2026-10-30, a confirmar no H1)
- [x] Precisa do app real no DevCenter agora? — Não. Tudo roda com o simulador (ADR-0016). O app (gratuito, 1 por titular no Brasil, redirect HTTPS) e o teste com usuário de teste real ficam como pendência do usuário antes do go-live. (Architect, 2026-10-30, a confirmar no H1)

## 9. Aprovação (H1)
Uma aprovação humana cobre o épico e as specs filhas apresentadas junto com ele. Registrada no frontmatter (`approved_by`, `approved_at`) do épico e de cada filha.

## 10. Registro de Impedimentos
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 11. Relatório de Entrega

## 12. Emendas
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

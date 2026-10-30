---
id: SPEC-0021
title: Gateway autenticado da API do Mercado Livre (renovação de token e resiliência)
tier: full
type: feature
user_facing: false
status: approved
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0026]
consumes_contract: [SPEC-0020@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/MercadoLivre/**, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.ArchitectureTests/**]
adrs: [ADR-0001, ADR-0006, ADR-0010, ADR-0014]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-30
---
# SPEC-0021 — Gateway autenticado da API do Mercado Livre (renovação de token e resiliência)

## 1. Visão Geral
Entrega o gateway autenticado do módulo `MercadoLivre`: a única porta para outros módulos lerem dados da API do ML em nome de uma conta conectada. Os tokens nunca saem do módulo.

O gateway:
- renova o access token antes de expirar e grava o novo refresh token, que é de uso único;
- marca a conta como "reconectar necessário" quando o ML recusa o refresh;
- aplica retry com `Retry-After`, timeout e circuit breaker;
- expõe a busca de IDs de anúncios (offset e `scan`) e a leitura em lote (`/items/bulk`).

## 2. Motivação & Escopo
**Motivação:** o access token do ML dura 6 h, e o refresh token é de uso único: cada renovação devolve um novo, e só o último vale. Renovações concorrentes ou retentadas invalidam a conta. A importação de anúncios (SPEC-0022), o webhook (SPEC-0023) e os épicos seguintes precisam de chamadas autenticadas confiáveis (ADR-0010).

**Objetivos (dentro do escopo):**
- `MercadoLivre.PublicApi`: interface `IMercadoLivreItems` (contrato abaixo), com tipos e exceções públicos.
- Provedor de token interno:
  - renova quando faltam menos de 5 min para expirar;
  - serializa por conta com lock de linha no PostgreSQL (`SELECT … FOR UPDATE`), relendo a conta depois de obter o lock;
  - grava os tokens novos cifrados;
  - nunca retenta o `POST /oauth/token` de refresh.
- Em `invalid_grant` no refresh:
  - status `reauthorization_required`, com os tokens apagados;
  - evento `MercadoLivreAccountReauthorizationRequired` v1 no outbox.
- Em 401 numa chamada da API: uma renovação forçada e uma nova tentativa. Novo 401 leva a "reconectar necessário".
- Cliente HTTP nomeado `mercadolivre-api` com resiliência:
  - retry de até 3 tentativas para 408/429/5xx/erro de rede, respeitando `Retry-After` (máx. 30 s);
  - timeout de 10 s por tentativa e de 60 s no total;
  - circuit breaker padrão do Http.Resilience.
- Busca de IDs:
  - `GET /users/{id}/items/search` com `status` e `limit=100`, paginando por offset;
  - se `paging.total > 1000`, recomeça com `search_type=scan` e segue o `scroll_id` até a lista acabar (null ou vazia).
- Leitura em lote: `GET /items/bulk?ids=` em lotes de no máximo `MercadoLivre:BulkBatchSize` (padrão 20), com seleção de campos `attributes=body.…`, mapeando `status_code` por item.
- Métricas:
  - `vitrine.mercadolivre.token_refreshes{result=renewed|reauthorization_required|unavailable}`;
  - duração das chamadas, pela instrumentação HTTP do OpenTelemetry já existente.

**Não-objetivos (fora do escopo):**
- Escrita no ML (título, descrição, respostas): épicos seguintes, que ampliam o `PublicApi` por emenda.
- Perguntas e pedidos.
- Proteger um refresh token já consumido quando a resposta se perde na rede. É limitação do protocolo do ML: a conta vai para "reconectar necessário" na próxima chamada, e o vendedor reconecta.

## 3. Dependências
- **Implementações necessárias:** N/A.
- **Contratos consumidos:** SPEC-0020@1, que fornece a tabela `accounts` (tokens cifrados, `access_token_expires_at`, `status`), a configuração `MercadoLivre:*` e o padrão de eventos do módulo. O gateway estende o mesmo módulo.
- **Pré-requisitos externos:** N/A (WireMock nos testes; simulador da SPEC-0024 no desenvolvimento).

## 4. Decisão Arquitetural
**Contexto:**
- Segue o módulo criado pela SPEC-0020 e o `ServiceDefaults` (`AddStandardResilienceHandler`, Microsoft.Extensions.Http.Resilience já na solução — ADR-0010).
- Clientes nomeados como em `ExternalHttpTests.cs` (SPEC-0003).
- Eventos pelo outbox como `WorkspaceCreated` (Identity).

**Decisão:**
- O gateway vive em `MercadoLivre.Infrastructure` e é exposto por interfaces em `MercadoLivre.PublicApi`.
- O token é anexado por um `DelegatingHandler` que chama o provedor de token.
- O refresh usa outro cliente nomeado (`mercadolivre-oauth`), sem retry.

**Justificativa:**
- A ADR-0001 permite leitura síncrona entre módulos pela `PublicApi`, e a ADR-0006 manda que os tokens não saiam do módulo.
- O lock de linha no PostgreSQL resolve a concorrência entre instâncias e entre handlers Wolverine, sem serviço novo.

**Desvio do padrão existente:** nenhum.

**Alternativas descartadas:**
- `SemaphoreSlim` em memória: não protege entre processos (migração, deploy com duas instâncias durante a troca).
- Renovar só ao receber 401: concentra falhas, e a renovação proativa reduz chamadas perdidas.
- Biblioteca de cliente do ML: não há SDK .NET oficial mantido (não verificado; nenhum citado na documentação consultada).

**ADRs:** ADR-0001, ADR-0006, ADR-0010, ADR-0014.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:**
  - a renovação acontece no máximo 1 vez por conta, mesmo com chamadas concorrentes — IT-03;
  - o lote do bulk tem até 20 IDs: 1000 anúncios = 50 chamadas;
  - overhead do gateway < 20 ms por chamada, sem contar a rede, medido no IT com WireMock local.
- **Segurança:**
  - token só no cabeçalho `Authorization` da chamada ao `ApiBaseUrl`; nunca em query, log ou exceção — IT-08;
  - o handler recusa anexar o token a outro host — UT-03.
- **Privacidade e dados pessoais:** N/A — não guarda dado novo. Os anúncios são dados do vendedor, tratados na SPEC-0022.
- **Disponibilidade e resiliência:**
  - retry com `Retry-After`, circuit breaker e timeouts (seção 2) — IT-04;
  - falha esgotada gera `MercadoLivreUnavailableException`, sem mudar o estado da conta;
  - refresh nunca retentado — IT-02.
- **Acessibilidade (UI):** N/A — sem tela.
- **Custo:** N/A — API do ML sem custo.

## 6. Artefato A — Contrato
**Interface:**
- `Vitrine.MercadoLivre.PublicApi.IMercadoLivreItems`
- evento `MercadoLivreAccountReauthorizationRequired` v1

```text
namespace Vitrine.MercadoLivre.PublicApi

interface IMercadoLivreItems
{
  // IDs (ex.: "MLB1234567890") dos anúncios da conta com o status pedido, em páginas (até 100 por página).
  // status: "active" | "paused" | "closed". Ordem não garantida; pode repetir IDs entre páginas (o consumidor deduplica).
  IAsyncEnumerable<IReadOnlyList<string>> SearchItemIdsAsync(Guid accountId, string status, CancellationToken ct);

  // Lê os anúncios em lote (/items/bulk), quebrando em lotes de BulkBatchSize. Um resultado por ID pedido, na mesma ordem.
  Task<IReadOnlyList<MercadoLivreItemResult>> GetItemsAsync(Guid accountId, IReadOnlyList<string> itemIds, CancellationToken ct);
}

abstract record MercadoLivreItemResult(string ItemId)
  sealed record Found(MercadoLivreItem Item)
  sealed record NotFound(string ItemId)        // status_code 404 no bulk
  sealed record Unavailable(string ItemId)     // outro status_code por item (ex.: 403); não lança

record MercadoLivreItem(
  string Id, long SellerId, string Title, decimal Price, string CurrencyId,
  int AvailableQuantity, int SoldQuantity, string Status,      // active | paused | closed | under_review | inactive ...
  Uri? Permalink, Uri? ThumbnailUrl, string? ListingTypeId, DateTimeOffset LastUpdated)

Exceções públicas (lançadas pelos dois métodos):
  MercadoLivreAccountNotFoundException(accountId)              conta inexistente (ex.: desconectada)
  MercadoLivreReauthorizationRequiredException(accountId)      conta já estava ou acabou de ficar em reauthorization_required
  MercadoLivreUnavailableException(accountId, reason)          retries esgotados, circuito aberto, timeout, resposta inválida

Chamadas ao ML (ApiBaseUrl, Bearer):
  GET /users/{mlUserId}/items/search?status={s}&limit=100&offset={n}
      resposta { results: [ids], paging: { total, offset, limit } }; se paging.total > 1000:
  GET /users/{mlUserId}/items/search?status={s}&limit=100&search_type=scan[&scroll_id={id}]
      resposta { results, scroll_id }; fim quando results vazio ou scroll_id null
  GET /items/bulk?ids={até 20, separados por vírgula}&attributes=body.id,body.seller_id,body.title,body.price,
      body.currency_id,body.available_quantity,body.sold_quantity,body.status,body.permalink,body.thumbnail,
      body.secure_thumbnail,body.listing_type_id,body.last_updated
      resposta [{ id, status_code, body: {...} }]
      ThumbnailUrl = secure_thumbnail; se ausente, thumbnail com esquema trocado para https; URL não-https ou fora de
      *.mlstatic.com -> null (a SPA só exibe imagens desse domínio — CSP)

Renovação (cliente mercadolivre-oauth, sem retry):
  POST /oauth/token  grant_type=refresh_token, client_id, client_secret, refresh_token  (form-urlencoded)
    200 -> grava access_token, refresh_token (novo), access_token_expires_at = agora + expires_in
    400 invalid_grant -> status = reauthorization_required, tokens apagados,
                         publica MercadoLivreAccountReauthorizationRequired; lança ReauthorizationRequired
    outro erro / rede -> nada muda; lança Unavailable

Evento (Vitrine.MercadoLivre.IntegrationEvents), v1:
  MercadoLivreAccountReauthorizationRequired { accountId: uuid, workspaceId: uuid, mlUserId: int64, occurredAt: datetime(UTC) }

Configuração nova: MercadoLivre:BulkBatchSize (padrão 20, 1..100), MercadoLivre:RefreshSkew (padrão 00:05:00).
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches`. São `MercadoLivre.PublicApi`, `MercadoLivre.Infrastructure` (provedor de token, handler, gateway, cliente HTTP) e `MercadoLivre.IntegrationEvents`.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Token válido | expira em mais de 5 min | chamada com `Authorization: Bearer <token>`, sem renovar | UT-01, IT-01 |
| Token perto de expirar | expira em menos de 5 min | renova uma vez, grava os tokens novos cifrados, segue a chamada | UT-01, IT-02 |
| Refresh recusado | `invalid_grant` | conta em `reauthorization_required`, tokens apagados, evento no outbox, `ReauthorizationRequired` lançada | IT-02, CT-01 |
| Refresh com falha de rede/5xx | erro no `/oauth/token` | nada muda, `Unavailable` lançada, sem retry do refresh | IT-02 |
| Concorrência | 5 chamadas paralelas com token expirado | exatamente 1 `POST /oauth/token`; todas usam o token novo | IT-03 |
| 429/5xx transitório | ML responde 429 com `Retry-After: 1` e depois 200 | espera e conclui | IT-04 |
| Falha persistente | 5xx em todas as tentativas | `Unavailable` após 3 tentativas; o circuito abre e as próximas chamadas falham rápido | IT-04 |
| 401 na API | token rejeitado | uma renovação forçada e uma nova tentativa; novo 401 leva a `ReauthorizationRequired` | IT-05 |
| Busca pequena | `paging.total` ≤ 1000 | pagina por offset até `total` | UT-02, IT-06 |
| Busca grande | `paging.total` > 1000 | recomeça com `scan` e segue o `scroll_id` até acabar | UT-02, IT-06 |
| Leitura em lote | 45 IDs | 3 chamadas `/items/bulk` (20+20+5); `Found`/`NotFound`/`Unavailable` por `status_code`, na ordem pedida | IT-07, CT-02 |
| Conta inexistente ou já em reauth | conta apagada ou com status `reauthorization_required` | `AccountNotFound` / `ReauthorizationRequired`, sem chamar o ML | IT-01 |
| Token fora do host do ML | requisição para outro host | o handler não anexa o token | UT-03 |
| Segredos fora de logs e exceções | qualquer fluxo | logs e mensagens de exceção sem token, refresh token nem client secret | IT-08 |
| Fronteiras | `PublicApi` | só tipos sem token; Listings consome só `PublicApi` | UT-04 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — módulo criado pela SPEC-0020, já coberto pelos testes dela.

### 7.2 Testes Unitários
- **UT-01** — Dado `access_token_expires_at` e um `TimeProvider` falso, quando o provedor decide se renova, então só renova com menos de `RefreshSkew` (5 min) restante, ou quando forçado após um 401.
- **UT-02** — Dado respostas de busca simuladas, quando o paginador as percorre, então:
  - com `total` ≤ 1000, usa offsets 0, 100, 200… até `total`;
  - com `total` > 1000, descarta a 1ª página e passa a `scan`, seguindo o `scroll_id` até `results` vazio ou `scroll_id` null;
  - nunca entra em laço quando o `scroll_id` se repete com `results` vazio.
- **UT-03** — Dado o handler de autenticação, quando a requisição vai para um host diferente de `ApiBaseUrl`, então não anexa `Authorization`.
- **UT-04** — Dado os assemblies, quando as regras de arquitetura rodam, então:
  - `MercadoLivre.PublicApi` só referencia BCL;
  - nenhum tipo público tem membro com "token"/"secret";
  - `Infrastructure` não é referenciado por outros módulos.

### 7.3 Testes de Integração
<!-- Postgres real + WireMock no lugar de api.mercadolibre.com (SPEC-0003 (IT 03)). Conta criada direto no banco pelo builder da SPEC-0020 (tokens cifrados). -->
- **IT-01** — Dado uma conta com token válido, quando `GetItemsAsync` é chamado, então o WireMock recebe `Authorization: Bearer <token>` e nenhum `/oauth/token`. Para conta inexistente, lança `AccountNotFound`. Para conta em `reauthorization_required`, lança `ReauthorizationRequired`. Nos dois últimos casos, sem chamada ao ML.
- **IT-02** — Dado uma conta com token expirando, quando há uma chamada:
  - com o refresh 200, os tokens novos ficam gravados cifrados (o refresh token novo substitui o antigo) e a chamada segue com o token novo;
  - com `invalid_grant`, a conta fica `reauthorization_required` com os tokens apagados, `MercadoLivreAccountReauthorizationRequired` vai para o outbox e a exceção é lançada;
  - com 500 no refresh, a conta não muda, `Unavailable` é lançada e o WireMock registrou exatamente 1 `POST /oauth/token`.
- **IT-03** — Dado uma conta com token expirado, quando 5 chamadas `GetItemsAsync` rodam em paralelo (escopos de DI distintos), então o WireMock registra exatamente 1 `POST /oauth/token`, e todas as 5 chamadas ao bulk usam o token novo.
- **IT-04** — Dado o WireMock:
  - respondendo 429 com `Retry-After: 1` e depois 200, a chamada conclui após ≥ 1 s;
  - respondendo 503 sempre, lança `Unavailable` depois de 3 tentativas, e as chamadas seguintes falham sem chegar ao WireMock enquanto o circuito estiver aberto.
- **IT-05** — Dado o bulk respondendo 401 uma vez, quando chamado, então há 1 renovação forçada e a chamada repetida conclui. Se o 401 se repete depois da renovação, então lança `ReauthorizationRequired` e a conta fica nesse status.
- **IT-06** — Dado a busca com `total=250`, quando `SearchItemIdsAsync` é chamado, então há 3 páginas por offset. Com `total=2300`, há páginas `scan` com `scroll_id` encadeado até `results` vazio, e todos os IDs são devolvidos.
- **IT-07** — Dado 45 IDs, com uma resposta de `/items/bulk` no formato documentado (`id`, `status_code`, `body`) e um ID com `status_code` 404, quando `GetItemsAsync` é chamado, então:
  - há 3 chamadas, com os IDs e `attributes` corretos;
  - devolve 44 `Found` com os campos mapeados (`Price` decimal, `SellerId` int64, `ThumbnailUrl` https de `*.mlstatic.com` ou null) e 1 `NotFound`, na ordem pedida.
- **IT-08** — Dado os fluxos de IT-02 e IT-05, quando os logs capturados e as mensagens das exceções são inspecionados, então não contêm access token, refresh token nem client secret.

### 7.4 Testes de Contrato
- **CT-01** — Dado uma renovação recusada, quando um assinante de teste recebe `MercadoLivreAccountReauthorizationRequired`, então o payload valida contra o schema v1 (seção 6). A SPEC-0022 consome esse contrato.
- **CT-02** — Dado a fixture de resposta do `/items/bulk` copiada da documentação do ML (versionada em `tests/.../Fixtures`, com URL e data), quando mapeada pelo gateway, então produz `MercadoLivreItem` com todos os campos do contrato. É o contrato do `PublicApi` consumido pela SPEC-0022, que testa contra um dublê dessa interface.

### 7.5 Testes E2E
- N/A — sem jornada de usuário própria. A jornada com renovação e reconexão é a SPEC-0025 (E2E 02), contra o simulador.

### 7.6 Outros
- N/A.

**Dublês e dados de teste:**
- WireMock com cenários de estado (`InScenario`) para a sequência 429→200 e 401→200;
- builder de conta com tokens cifrados;
- `TimeProvider` falso;
- IDs `MLB` sintéticos.

**Ambiente de execução:** IT com Testcontainers e WireMock (local e CI).

## 8. Plano de Rollout
- **Estratégia:** deploy direto. Só é usado pela SPEC-0022/0023.
- **Dados/schema:** sem migração nova (usa as colunas da SPEC-0020).
- **Compatibilidade:** `PublicApi` novo, v1. Mudança incompatível exige emenda e versão nova.
- **Observabilidade:**
  - contador `vitrine.mercadolivre.token_refreshes{result}`;
  - spans HTTP do gateway com `ml.endpoint` (sem query de IDs longa) e `accountId`;
  - log Warning ao entrar em `reauthorization_required`.
- **Rollback:** revert do merge.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Limite de IDs por chamada do `/items/bulk`? — Não verificado na documentação (30/10/2026). Usa 20 (limite documentado do multiget antigo), configurável por `MercadoLivre:BulkBatchSize`. (Architect, 2026-10-30, a confirmar no H1)
- [x] Retentar o refresh do token? — Não. O refresh token é de uso único, e retentar depois de uma resposta perdida invalidaria a conta da mesma forma; falhar rápido mantém o estado consistente. (Architect, 2026-10-30, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Política de token e mapeamento**
- [ ] Red: escrever UT-01..UT-04 com a tag `SPEC-0021:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: decisão de renovação antecipada, mapeamento de erros do ML para exceções públicas e filtro de miniatura `*.mlstatic.com`
- [ ] Refactor mantendo tudo verde

**Fase 2: Gateway com WireMock**
- [ ] Red: escrever IT-01..IT-08, CT-01 e CT-02 com a tag `SPEC-0021:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `IMercadoLivreItems` com refresh sob lock `FOR UPDATE` sem retry, `reauthorization_required` + evento, cliente `mercadolivre-api` resiliente, busca com `scan` e `/items/bulk` em lotes de 20
- [ ] Refactor mantendo tudo verde
- [ ] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — ddc6ebd (árvore suja) | 2026-10-30 |
| G1 Red | PENDING | | |
| G2 Green | PENDING | | |
| G3 Arquitetura | PENDING | | |
| G4 Review | PENDING | | |
| G5 Integração & CI | PENDING | | |
| H2 Integração aprovada | PENDING | | |
| G6 Deploy | PENDING | | |
| G7 Pronto & Docs | PENDING | | |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
<!-- comportamento entregue do ponto de vista do usuário/sistema -->

### Como foi feito
<!-- decisões de implementação, módulos/arquivos principais, desvios e emendas (com versão), dívidas assumidas -->

### Prova de Correção
<!-- type fix: o teste de regressão falhou antes da correção (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->

### Verificação
<!-- Uma linha por teste do plano (todos os IDs da seção 7). Resultado: PASS. Evidência: execução de CI, commit ou relatório. -->
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|

### Definição de Pronto
- [ ] Todos os testes do plano passando e listados na Verificação
- [ ] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [ ] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [ ] Review independente sem achados blocker/major (G4)
- [ ] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [ ] Requisitos não-funcionais medidos com evidência (ou N/A justificado)
- [ ] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6)
- [ ] Observabilidade e rollback prontos conforme o Plano de Rollout
- [ ] Documentação raiz e CHANGELOG atualizados (G7)
- [ ] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7)
- [ ] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0021`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

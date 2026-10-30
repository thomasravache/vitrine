---
id: SPEC-0021
title: Gateway autenticado da API do Mercado Livre (renovação de token e resiliência)
tier: full
type: feature
user_facing: false
status: implemented
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0026]
consumes_contract: [SPEC-0020@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/MercadoLivre/**, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.ArchitectureTests/**, src/Api/Observability/ObservabilityExtensions.cs]
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
  - retry com até 3 tentativas **no total** por chamada (1 + 2 retries, `MaxRetryAttempts = 2`) para 408/429/5xx/erro de rede, respeitando `Retry-After` (máx. 30 s);
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
  Inclui: `MercadoLivre:BulkBatchSize` fora de 1..100 reprova a validação das opções.
- **UT-04** — Dado os assemblies, quando as regras de arquitetura rodam, então:
  - `MercadoLivre.PublicApi` só referencia BCL;
  - nenhum tipo público tem membro com "token"/"secret";
  - `Infrastructure` não é referenciado por outros módulos.

### 7.3 Testes de Integração
<!-- Postgres real + WireMock no lugar de api.mercadolibre.com (SPEC-0003 (IT 03)). Conta criada direto no banco pelo DbContext do módulo, com tokens cifrados (suporte `MercadoLivreGateway` dos testes). -->
- **IT-01** — Dado uma conta com token válido, quando `GetItemsAsync` é chamado, então o WireMock recebe `Authorization: Bearer <token>` e nenhum `/oauth/token`. Para conta inexistente, lança `AccountNotFound`. Para conta em `reauthorization_required`, lança `ReauthorizationRequired`. Nos dois últimos casos, sem chamada ao ML.
- **IT-02** — Dado uma conta com token expirando, quando há uma chamada:
  - com o refresh 200, os tokens novos ficam gravados cifrados (o refresh token novo substitui o antigo) e a chamada segue com o token novo;
  - com `invalid_grant`, a conta fica `reauthorization_required` com os tokens apagados, `MercadoLivreAccountReauthorizationRequired` vai para o outbox e a exceção é lançada;
  - com 500 no refresh, a conta não muda, `Unavailable` é lançada e o WireMock registrou exatamente 1 `POST /oauth/token`.
- **IT-03** — Dado uma conta com token expirado, quando 5 chamadas `GetItemsAsync` rodam em paralelo (escopos de DI distintos), então o WireMock registra exatamente 1 `POST /oauth/token`, e todas as 5 chamadas ao bulk usam o token novo.
  Inclui: cada renovação incrementa `vitrine.mercadolivre.token_refreshes{result}` com `renewed`, `reauthorization_required` ou `unavailable`.
- **IT-04** — Dado o WireMock:
  - respondendo 429 com `Retry-After: 1` e depois 200, a chamada conclui após ≥ 1 s;
  - respondendo 503 sempre, lança `Unavailable` depois de exatamente 3 requisições, e as chamadas seguintes falham sem chegar ao WireMock enquanto o circuito estiver aberto. Para tornar o circuito observável, só o host deste teste reduz `MinimumThroughput` e o atraso base do retry; contagem de tentativas e timeouts são os do módulo. Também prova o teto de 30 s do `Retry-After` (valor maior é limitado) e o timeout por tentativa.
- **IT-05** — Dado o bulk respondendo 401 uma vez, quando chamado, então há 1 renovação forçada e a chamada repetida conclui. Se o 401 se repete depois da renovação, então lança `ReauthorizationRequired` e a conta fica nesse status.
- **IT-06** — Dado a busca com `total=250`, quando `SearchItemIdsAsync` é chamado, então há 3 páginas por offset. Com `total=2300`, há páginas `scan` com `scroll_id` encadeado até `results` vazio, e todos os IDs são devolvidos.
- **IT-07** — Dado 45 IDs, com uma resposta de `/items/bulk` no formato documentado (`id`, `status_code`, `body`) e um ID com `status_code` 404, quando `GetItemsAsync` é chamado, então:
  - há 3 chamadas, com os IDs e `attributes` corretos;
  - devolve 44 `Found` com os campos mapeados (`Price` decimal, `SellerId` int64, `ThumbnailUrl` https de `*.mlstatic.com` ou null) e 1 `NotFound`, na ordem pedida.
- **IT-08** — Dado os fluxos de IT-02 e IT-05, quando os logs capturados e as mensagens das exceções são inspecionados, então não contêm access token, refresh token nem client secret.

### 7.4 Testes de Contrato
- **CT-01** — Dado uma renovação recusada, quando um assinante de teste recebe `MercadoLivreAccountReauthorizationRequired`, então o payload valida contra o schema v1 (seção 6). A SPEC-0022 consome esse contrato.
- **CT-02** — Dado a fixture de resposta do `/items/bulk` no formato do §6 (versionada em `tests/Vitrine.IntegrationTests/MercadoLivre/Fixtures`, com a origem anotada; a SPEC-0024 a substitui pela cópia da documentação na pasta compartilhada `tests/Fixtures/MercadoLivre/`), quando mapeada pelo gateway, então produz `MercadoLivreItem` com todos os campos do contrato. É o contrato do `PublicApi` consumido pela SPEC-0022, que testa contra um dublê dessa interface.

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
- [x] Red: escrever UT-01..UT-04 com a tag `SPEC-0021:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: decisão de renovação antecipada, mapeamento de erros do ML para exceções públicas e filtro de miniatura `*.mlstatic.com`
- [x] Refactor mantendo tudo verde

**Fase 2: Gateway com WireMock**
- [x] Red: escrever IT-01..IT-08, CT-01 e CT-02 com a tag `SPEC-0021:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `IMercadoLivreItems` com refresh sob lock `FOR UPDATE` sem retry, `reauthorization_required` + evento, cliente `mercadolivre-api` resiliente, busca com `scan` e `/items/bulk` em lotes de 20
- [x] Refactor mantendo tudo verde
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like com smoke (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — ddc6ebd (árvore suja) | 2026-10-30 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 2d83d7e | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  536ms (environment 63%, transform 16%, import 12%, tests 5%, worker 4%)); lint exit 0 ([0m[34mChecked 40 files in 22[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 86%) — f280c04 | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 303ms) — f280c04 | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED_WITH_NOTES (3ª rodada; 0 blocker/major; risco aceito: refresh 200 + commit falho → log Critical e reconexão na próxima renovação; nit de log duplicado) — f280c04 | 2026-10-30 |
| G5 Integração & CI | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  374ms (environment 58%, import 19%, transform 17%, tests 4%, worker 2%)); test_integration exit 0 (duration: 2m 37s 788ms); test_e2e exit 0 (30 passed (31.0s)); arch_test exit 0 (duration: 1s 331ms); security_scan exit 0 ([90m12:04PM[0m [32mINF[0m [1mno leaks found[0m); nota: PR #49 CI 8/8 verde + CodeQL (runs/41075612230) — 9506633 | 2026-10-30 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-30 |
| G6 Deploy | PASS | MANUAL: Deploy local prod-like (deploy/deploy.sh 0b17e24 --env-file deploy/local/.env): health 200 em 9 s; smoke @smoke 2/2; 0 logs Error/Critical; gateway sem endpoint público (exercitado pela SPEC-0022 e pelo simulador da SPEC-0024); staging/produção na SPEC-0009 | 2026-10-30 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — de00072 | 2026-10-30 |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-30 | G1 | spec | Contagem de tentativas ambígua (até 3 tentativas vs MaxRetryAttempts=3 = 4 requisições); circuit breaker padrão inobservável em IT (MinimumThroughput 100); procedência da fixture do CT-02 (doc do ML 403 ao WebFetch; pasta compartilhada fora de touches); 'builder da SPEC-0020' inexistente; métrica, timeouts, teto do Retry-After e validação de BulkBatchSize sem teste no plano | Test-writer assumiu 3 requisições no total, ajustou o circuito só no host do IT-04 e versionou a fixture em tests/Vitrine.IntegrationTests/MercadoLivre/Fixtures | Architect | Emenda v1.1 — tentativas, circuito no IT-04, cobertura de métrica/BulkBatchSize/Retry-After e fixture do CT-02 | 2026-10-30 |

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
Gateway interno `IMercadoLivreItems` para os módulos consumirem a API do ML em nome de uma conta conectada: busca de IDs dos anúncios do vendedor (offset até 1000 e `search_type=scan` acima disso) e leitura em lote pelo `/items/bulk` (substituto do `/items?ids=` descontinuado). O access token é renovado antes de expirar, com lock por conta e sem nunca repetir o refresh (o refresh token do ML é de uso único); `invalid_grant` ou um segundo 401 marcam a conta para reconexão e publicam `MercadoLivreAccountReauthorizationRequired` v1. Sem endpoint público novo.

### Como foi feito
- `src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/Api/`: `MercadoLivreItemsGateway`, `MercadoLivreAccessTokenProvider` (transação com `SET LOCAL lock_timeout = '15s'` + `SELECT … FOR UPDATE` e releitura; do envio do `POST /oauth/token` até o commit usa `CancellationToken.None`), `MercadoLivreTokenRefresher` (cliente `mercadolivre-oauth` sem retry; toda falha vira `Unavailable`), `MercadoLivreAuthenticationHandler` (dentro do pipeline de resiliência, Bearer só para `ApiBaseUrl`), `ItemSearchPaginator`, `BulkItemMapper`, `MercadoLivreTracing` (spans com `ml.endpoint` e `ml.account_id`).
- Cliente `mercadolivre-api` com `AddStandardResilienceHandler`: 3 tentativas no total, `Retry-After` limitado a 30 s por `DelayGenerator`, 10 s por tentativa, 60 s no total, circuit breaker padrão.
- Emendas: v1.1 (tentativas, observabilidade do circuito no IT-04, cobertura de métrica/`BulkBatchSize`/`Retry-After`, fixture do CT-02 no formato do §6) e v1.2 (`touches` com `src/Api/Observability/ObservabilityExtensions.cs` para o `ActivitySource`).
- G4 em 3 rodadas: corrigidos o refresh retentado por timeout da tentativa (major), a guarda de laço do scan que quebrava `scroll_id` constante (major), `ReauthorizationRequired` com token já substituído, item malformado derrubando o lote, falta de log/spans e tipos públicos (agora `internal` com `InternalsVisibleTo`).
- Desvio já conhecido: `RemoveAllResilienceHandlers()` (EXTEXP0001) nos clientes do módulo, como na SPEC-0020.
- **Riscos aceitos:** (1) se o refresh devolve 200 e o commit falha, sai log `Critical` ("renovação obtida mas não gravada") e a conta pede reconexão na próxima renovação — repetir o save exigiria nova transação e novo lock; (2) depois de enviado o POST, a renovação não é cancelável (até 10 s de OAuth + command timeout do EF); (3) um 401 espúrio logo após uma renovação válida leva à reconexão, como a spec define; (4) nesse caso (1) também sai um log Error "a conta não mudou" (nit de redação).

### Prova de Correção
N/A — `type: feature`.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| UT-01 | Renovação só com menos de `RefreshSkew` restante ou forçada após 401 | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| UT-02 | Paginação por offset até 1000 e `scan` com `scroll_id` (constante ou não), sem laço | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| UT-03 | Token só para o host de `ApiBaseUrl`; `BulkBatchSize` validado em 1..100 | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| UT-04 | Arquitetura: PublicApi sem tokens, sem referência a camadas internas | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-01 | Token perto de expirar é renovado uma vez, com Bearer novo na chamada | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-02 | Falha do refresh (500 ou lentidão) vira `Unavailable` com 1 único POST e conta inalterada | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-03 | `invalid_grant` → `reauthorization_required`, tokens apagados, evento na outbox; métrica `token_refreshes{result}` | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-04 | 3 tentativas, `Retry-After` limitado a 30 s, 10 s por tentativa, 60 s no total, circuito aberto | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-05 | 401 → 1 renovação forçada; 2º 401 → `ReauthorizationRequired` com log Warning; token já substituído → `Unavailable` | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-06 | Busca de IDs por offset e por scan acima de 1000 anúncios | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-07 | `/items/bulk` em lotes, ordem preservada, 404 → `NotFound`, item malformado → `Unavailable` só para ele | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| IT-08 | Tokens e client secret fora de logs, exceções e respostas | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| CT-01 | Evento `MercadoLivreAccountReauthorizationRequired` v1 conforme o contrato | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |
| CT-02 | Fixture do `/items/bulk` mapeada para `MercadoLivreItem` com todos os campos | PASS | CI PR #49 runs/41075612230 (f280c04); main 0b17e24 |

### Definição de Pronto
- [x] Todos os testes do plano passando e listados na Verificação
- [x] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [x] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado — fronteiras do módulo, outbox e cifra padrão (SPEC-0026); `RemoveAllResilienceHandlers` registrado como desvio pontual
- [x] Requisitos não-funcionais medidos com evidência (ou N/A justificado) — tentativas, timeouts e teto do `Retry-After` (IT-04), sem segredos em logs (IT-08), int64 nos IDs
- [x] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6) — ambiente local prod-like; staging/produção na SPEC-0009
- [x] Observabilidade e rollback prontos conforme o Plano de Rollout — métrica `token_refreshes`, logs Warning/Critical sem tokens, spans `Vitrine.MercadoLivre`; rollback por revert (sem migração)
- [x] Documentação raiz e CHANGELOG atualizados (G7)
- [x] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7) — N/A: sem `living_docs`
- [x] Pendências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, imagem `ghcr.io/thomasravache/vitrine-api:0b17e24`, 2026-10-30, `deploy/deploy.sh` (health 200 em 9 s, smoke 2/2, 0 logs Error/Critical). O gateway não tem endpoint público; é exercitado pela SPEC-0022 e, no ambiente local, pelo simulador da SPEC-0024. Staging/produção na SPEC-0009.

### Pendências
- Fixture do CT-02 substituída pela cópia da documentação na SPEC-0024 (pasta compartilhada `tests/Fixtures/MercadoLivre/`).

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0021`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (contrato inalterado) | 2026-10-30 | 3 tentativas no total (2 retries); IT-04 pode reduzir `MinimumThroughput`/atraso base só no seu host e cobre o teto do `Retry-After` e o timeout por tentativa; IT-03 cobre a métrica e UT-03 a validação de `BulkBatchSize`; fixture do CT-02 no formato do §6 até a SPEC-0024 trazer a cópia da documentação | Ambiguidades apontadas pelo Test-writer no G1 (IMP-01) | SPEC-0024 (substitui a fixture) | thomas (piloto automático do épico SPEC-0019) |
| v1.2 (contrato inalterado) | 2026-10-30 | `touches` inclui `src/Api/Observability/ObservabilityExtensions.cs` (registro do `ActivitySource` `Vitrine.MercadoLivre` no OpenTelemetry, onde o meter do módulo já é registrado) | Spans do Plano de Rollout §8 pedidos no G4 | nenhuma | thomas (piloto automático do épico SPEC-0019) |

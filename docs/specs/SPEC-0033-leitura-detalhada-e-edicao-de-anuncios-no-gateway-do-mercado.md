---
id: SPEC-0033
title: Leitura detalhada e edição de anúncios no gateway do Mercado Livre
tier: full
type: feature
user_facing: false
status: approved
created: 2026-11-02
parent: SPEC-0032
depends_on: []
consumes_contract: [SPEC-0020@1, SPEC-0021@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/MercadoLivre/Vitrine.MercadoLivre.PublicApi/**, src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/**, tests/Vitrine.UnitTests/MercadoLivre/**, tests/Vitrine.IntegrationTests/MercadoLivre/**, tests/Vitrine.ArchitectureTests/MercadoLivreModuleArchitectureTests.cs, tests/Fixtures/MercadoLivre/**]
adrs: [ADR-0001, ADR-0006, ADR-0010]
external: []
size: M
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0033 — Leitura detalhada e edição de anúncios no gateway do Mercado Livre

<!-- type: feature | fix | refactor | migration | foundation. user_facing: true quando a mudança altera uma jornada do usuário (UI ou API pública) — exige teste E2E. Substitua todos os marcadores com chaves duplas: o G0 (`spec_graph.py validate`) reprova a spec enquanto restar algum. As seções de Checklist em diante são preenchidas depois da aprovação, sem marcadores. -->

## 1. Visão Geral
Amplia o gateway do módulo `MercadoLivre` com dois contratos novos no `PublicApi`, sem mexer no `IMercadoLivreItems` v1:
- **`IMercadoLivreListingDetails`:** lê o anúncio completo (categoria, atributos, vendas, catálogo, estado e descrição) e as regras da categoria (limites de título e descrição, atributos com `required`/`read_only`/`hidden`, valores permitidos e unidades), com cache de 24 h por categoria;
- **`IMercadoLivreListingEditor`:** publica no ML as mudanças aprovadas de título, descrição e atributos e devolve o **resultado por campo** (`Applied`, `Rejected(code, message)` ou `NotAttempted(reason)`).

As regras locais do ML (título travado com vendas ou catálogo, descrição só em texto puro, atributo só com valor permitido) ficam numa classe pura do `PublicApi`, `ListingChangeRules`. O editor as aplica antes de chamar o ML, e o módulo `Listings` usa a mesma regra para validar a sugestão da IA (SPEC-0035) e os valores finais do vendedor (SPEC-0036).

## 2. Motivação & Escopo
**Motivação:** o épico SPEC-0032 precisa ler o anúncio inteiro para gerar a sugestão e escrever no ML depois da aprovação do vendedor. Hoje o gateway só lê o resumo do anúncio (`/items/bulk`, SPEC-0021) e não escreve nada. Escrever no ML é a operação mais cara de errar no produto (ADR-0002): um PUT repetido, um campo enviado sem aprovação ou um erro mal traduzido viram anúncio quebrado na loja do vendedor.

**Objetivos (dentro do escopo):**
- `IMercadoLivreListingDetails`:
  - `GetItemDetailsAsync`: `GET /items/{id}` (seleção de campos) + `GET /items/{id}/description`. Anúncio de outro vendedor (`seller_id` diferente do `ml_user_id` da conta) é tratado como inexistente, a mesma guarda da SPEC-0029;
  - `GetCategoryAsync`: `GET /categories/{id}` + `GET /categories/{id}/attributes`, com cache em memória de 24 h por categoria (só respostas 200).
- `IMercadoLivreListingEditor.ApplyAsync`:
  - relê o anúncio e a categoria antes de escrever (a verdade é o ML, não o que o chamador mandou);
  - aplica `ListingChangeRules`; campo que viola a regra não é enviado (`NotAttempted` com o código);
  - título e atributos num único `PUT /items/{id}`; depois a descrição por `PUT /items/{id}/description?api_version=2` (quando já existe) ou `POST /items/{id}/description` (quando não existe);
  - erros do ML traduzidos para códigos estáveis: `FIELD_NOT_UPDATABLE`, `DESCRIPTION_INVALID`, `ITEM_NOT_ACTIVE`, `ML_CONFLICT`, `ML_FORBIDDEN`, `ML_REJECTED`, `ML_UNAVAILABLE`;
  - 409 de *optimistic locking*: uma única nova tentativa depois de 2 s.
- `ListingChangeRules` (pura, sem I/O) no `PublicApi`.
- Pipeline do cliente `mercadolivre-api`: `Retry.DisableForUnsafeHttpMethods()`. POST e PUT nunca são retentados pelo pipeline; a única repetição de escrita é a do 409 explícito, mais o reenvio depois de um 401 com renovação forçada (o 401 garante que nada foi aplicado), como nas leituras da SPEC-0021.
- Escopo `write`: o OAuth do ML não recebe escopo na URL de autorização (é configurado no app do DevCenter, SPEC-0020 §2). A troca do code passa a registrar log Warning `MissingWriteScope` quando o `scope` devolvido não contém `write`. O editor traduz 403 para `ML_FORBIDDEN`.
- Fixtures sintéticas em `tests/Fixtures/MercadoLivre/` (item com atributos, descrição, categoria, atributos da categoria, corpos de PUT/POST e erros), com a origem anotada.
- Métrica `vitrine.mercadolivre.listing_edits{field,result,code}` e spans com o caminho modelo (`/items/{id}`, `/items/{id}/description`, `/categories/{id}`, `/categories/{id}/attributes`).

**Não-objetivos (fora do escopo):**
- Nota de qualidade do anúncio (`GET /item/{id}/performance`): épico futuro.
- User products (`PUT /items/{id}/family_name`): o título de anúncio com `user_product_id` é bloqueado (`TITLE_LOCKED_USER_PRODUCT`); a edição pelo `family_name` fica para depois.
- Fotos, preço, estoque, variações e vídeo.
- Mudar o pedido de autorização do OAuth (os escopos vêm do app no DevCenter).
- Endpoint HTTP próprio: os contratos são internos, consumidos pelo `Listings`.

## 3. Dependências
- **Implementações necessárias:** N/A.
- **Contratos consumidos:**
  - SPEC-0020@1: tabela `accounts` (`ml_user_id`, status), configuração `MercadoLivre:*` e troca do code (onde entra o aviso do escopo `write`).
  - SPEC-0021@1: cliente `mercadolivre-api` (autenticação, renovação, resiliência), exceções públicas (`MercadoLivreAccountNotFoundException`, `MercadoLivreReauthorizationRequiredException`, `MercadoLivreUnavailableException`) e padrão de spans/métricas.
- **Pré-requisitos externos:** N/A. WireMock nos testes; o simulador ganha os mesmos endpoints na SPEC-0034.

## 4. Decisão Arquitetural
**Contexto:** segue o gateway da SPEC-0021:
- `src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/Api/MercadoLivreItemsGateway.cs` (chamada autenticada, mapeamento para exceções, span por chamada);
- `BulkItemMapper.cs` (mapeamento tolerante do JSON do ML);
- `MercadoLivreModule.cs` (registro do cliente `mercadolivre-api` e de `IMercadoLivreItems` como `Scoped`, `AlwaysUseServiceLocationFor` para os handlers do Wolverine);
- `PublicApi/IMercadoLivreItems.cs` e `MercadoLivreExceptions.cs` (tipos públicos sem token).

**Decisão:**
- Dois gateways internos novos (`MercadoLivreListingDetailsGateway`, `MercadoLivreListingEditor`) no `Infrastructure/Api`, sobre o mesmo cliente `mercadolivre-api` e o mesmo `MercadoLivreAuthenticationHandler`.
- `ListingChangeRules` como classe estática pública e pura no `PublicApi`: uma regra única para o editor (antes de chamar o ML) e para o `Listings` (validação pós-IA e revalidação do apply), sem I/O.
- Cache de categoria em `IMemoryCache` (já disponível pelo `Microsoft.AspNetCore.App`), chave `ml:category:{id}`, 24 h absolutos.
- O retry do pipeline passa a ignorar métodos não seguros (`DisableForUnsafeHttpMethods`), o que não muda as leituras (todas GET).

**Justificativa:**
- A ADR-0001 permite leitura e escrita síncronas entre módulos pela `PublicApi`, e a ADR-0006 manda que os tokens não saiam do módulo.
- A ADR-0010 exige idempotência: um PUT repetido depois de um timeout pode aplicar a mudança duas vezes ou sobre um estado que o vendedor não viu. Por isso, só se repete o 409 (que o ML documenta como "tente de novo") e o 401 (que garante que nada foi aplicado).
- Uma regra pura e compartilhada impede que a IA proponha, ou o vendedor aprove, algo que o editor vai recusar.

**Desvio do padrão existente:** nenhum. `ListingChangeRules` é código no `PublicApi`, que hoje só tem contratos; continua referenciando só a BCL (regra da SPEC-0021, UT-04).

**Alternativas descartadas:**
- Ampliar o `IMercadoLivreItems` v1: quebraria os dublês e o CT dos consumidores (SPEC-0022) e mistura leitura em lote com detalhe.
- Ler atributos e descrição pelo `/items/bulk`: o bulk não traz a descrição e devolveria campos pesados para a sincronização inteira.
- Validar só no `Listings`: o editor ficaria sem defesa contra chamadores futuros.
- Retentar o PUT pelo pipeline padrão: risco de aplicação dupla (ADR-0010).

**ADRs:** ADR-0001, ADR-0006, ADR-0010.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:**
  - `GetCategoryAsync` com cache: no máximo 2 chamadas ao ML por categoria a cada 24 h por instância — IT-02;
  - `ApplyAsync` faz no máximo 2 leituras (item e descrição) + 1 de categoria fora do cache + 2 escritas, e 1 escrita extra no 409 ou na recusa só do título;
  - overhead do gateway < 20 ms por chamada, sem contar a rede (mesma medida da SPEC-0021, no IT com WireMock local).
- **Segurança:**
  - token só no cabeçalho `Authorization` para o `ApiBaseUrl` (handler da SPEC-0021);
  - o editor recusa anúncio de outro vendedor (`MercadoLivreItemNotFoundException`, sem escrita) — IT-01;
  - nada sai sem passar por `ListingChangeRules` — IT-04;
  - logs e exceções sem token — IT-07.
- **Privacidade e dados pessoais:** N/A — título, descrição e atributos são dados públicos do anúncio do vendedor. A regra recusa dados de contato na descrição (regra do ML), o que também evita expor contato pessoal.
- **Disponibilidade e resiliência:**
  - leitura: o retry, os timeouts e o circuit breaker da SPEC-0021; falha esgotada lança `MercadoLivreUnavailableException`, e nada é escrito — IT-07;
  - escrita: sem retry do pipeline; 409 com uma nova tentativa depois de 2 s — IT-06; falha de rede ou 5xx numa escrita marca o campo como `Rejected(ML_UNAVAILABLE)` e interrompe as escritas seguintes (`NotAttempted(ML_UNAVAILABLE)`) — IT-07;
  - timeout numa escrita tem resultado incerto: o editor devolve `ML_UNAVAILABLE`, e o chamador confirma relendo o anúncio (SPEC-0036).
- **Acessibilidade (UI):** N/A — sem tela.
- **Custo:** N/A — API do ML sem custo; o cache de categoria reduz chamadas.

## 6. Artefato A — Contrato
**Interface:**
- `Vitrine.MercadoLivre.PublicApi.IMercadoLivreListingDetails`
- `Vitrine.MercadoLivre.PublicApi.IMercadoLivreListingEditor`
- `Vitrine.MercadoLivre.PublicApi.ListingChangeRules`

```text
namespace Vitrine.MercadoLivre.PublicApi

interface IMercadoLivreListingDetails
{
  // GET /items/{id}?attributes=id,seller_id,category_id,title,sold_quantity,status,catalog_listing,user_product_id,
  //     permalink,last_updated,attributes
  // + GET /items/{id}/description (404 -> Description = null).
  // null: anúncio inexistente (404) ou de outro vendedor (seller_id != ml_user_id da conta).
  Task<MercadoLivreItemDetails?> GetItemDetailsAsync(Guid accountId, string itemId, CancellationToken ct);

  // GET /categories/{id} + GET /categories/{id}/attributes; cache de 24 h por categoria (só 200). null: 404.
  Task<MercadoLivreCategory?> GetCategoryAsync(Guid accountId, string categoryId, CancellationToken ct);
}

record MercadoLivreItemDetails(
  string Id, long SellerId, string CategoryId, string Title, int SoldQuantity, string Status,
  bool CatalogListing, string? UserProductId, IReadOnlyList<MercadoLivreItemAttribute> Attributes,
  string? Description,               // plain_text; null quando o anúncio não tem descrição
  DateTimeOffset LastUpdated)
record MercadoLivreItemAttribute(string Id, string Name, string? ValueId, string? ValueName)

record MercadoLivreCategory(
  string Id, string Name, int MaxTitleLength,          // settings.max_title_length
  int? MaxDescriptionLength,                            // settings.max_description_length (null quando ausente)
  IReadOnlyList<MercadoLivreCategoryAttribute> Attributes)
record MercadoLivreCategoryAttribute(
  string Id, string Name, string ValueType,             // string | number | number_unit | boolean | list ...
  bool Required,                                        // tags.required || tags.catalog_required
  bool ReadOnly, bool Hidden,                           // tags.read_only, tags.hidden
  IReadOnlyList<MercadoLivreAttributeValue> Values,     // vazio = valor livre
  IReadOnlyList<string> AllowedUnits)                   // allowed_units[].id
record MercadoLivreAttributeValue(string Id, string Name)

interface IMercadoLivreListingEditor
{
  // Relê item e categoria, aplica ListingChangeRules, escreve no ML e devolve o resultado por campo.
  // Lança: AccountNotFound, ReauthorizationRequired, MercadoLivreItemNotFoundException (404 ou outro vendedor),
  //        Unavailable (só quando a releitura inicial falha: nada foi escrito).
  Task<ListingChangeResult> ApplyAsync(Guid accountId, string itemId, ListingChanges changes, CancellationToken ct);
}

record ListingChanges(string? Title, string? Description, IReadOnlyList<AttributeChange>? Attributes)   // null = não mexer
record AttributeChange(string Id, string? ValueId, string? ValueName)
record ListingChangeResult(FieldResult? Title, FieldResult? Description,
                           IReadOnlyDictionary<string, FieldResult> Attributes)   // por id de atributo enviado
abstract record FieldResult
  sealed record Applied
  sealed record Rejected(string Code, string Message)    // o ML recusou ou falhou
  sealed record NotAttempted(string Reason)              // não enviado: regra local ou passo anterior interrompido

sealed class MercadoLivreItemNotFoundException(Guid accountId, string itemId) : Exception   // nova, pública

static class ListingChangeRules   // pura, sem I/O
{
  const int DefaultMaxDescriptionLength = 50000;
  const int MaxFreeAttributeLength = 255;               // não verificado no ML; conferir no go-live
  string? TitleLockReason(MercadoLivreItemDetails item);
     // TITLE_LOCKED_SOLD (sold_quantity > 0) | TITLE_LOCKED_CATALOG (catalog_listing) |
     // TITLE_LOCKED_USER_PRODUCT (user_product_id) | null
  IReadOnlyDictionary<string, string> Violations(MercadoLivreItemDetails item, MercadoLivreCategory category,
                                                ListingChanges changes);
     // chave: "item" | "title" | "description" | "attributes.{id}"; valor: código
     // item:        ITEM_NOT_ACTIVE (status != active) — vale para todos os campos
     // title:       TITLE_LOCKED_* | TITLE_EMPTY | TITLE_TOO_LONG (> MaxTitleLength)
     // description: DESCRIPTION_INVALID (tag HTML, entidade HTML, emoji) | DESCRIPTION_CONTACT (e-mail, telefone, URL,
     //              "whatsapp") | DESCRIPTION_TOO_LONG (> MaxDescriptionLength ?? 50000); quebra "\n" é permitida
     // attributes:  ATTRIBUTE_UNKNOWN | ATTRIBUTE_NOT_EDITABLE (read_only ou hidden) |
     //              ATTRIBUTE_VALUE_NOT_ALLOWED (fora de Values; unidade fora de AllowedUnits; sem valor; texto
     //              livre inválido ou > 255)
  AttributeChange Normalize(MercadoLivreCategoryAttribute spec, AttributeChange change);
     // ValueName igual a um Values[].Name (sem diferenciar maiúsculas e acentos) vira ValueId
}

Escrita (cliente mercadolivre-api, Bearer, sem retry do pipeline):
  1. GetItemDetailsAsync + GetCategoryAsync (frescos para o item; categoria do cache)
  2. Violations -> campos com violação: NotAttempted(código); "item" -> todos NotAttempted(ITEM_NOT_ACTIVE), sem escrita
  3. PUT /items/{id}  { "title"?: string, "attributes"?: [{ "id", "value_id"? | "value_name"? }] }   (se houver campo)
       200 -> Applied
       400 com cause de título não modificável e atributos no corpo -> título Rejected(FIELD_NOT_UPDATABLE);
           reenvia uma vez só com os atributos
       400 -> Rejected(código mapeado) para os campos do corpo
       409 -> espera 2 s e reenvia uma vez; 2º 409 -> Rejected(ML_CONFLICT)
       401 -> renovação forçada e um reenvio (SPEC-0021); 2º 401 -> ReauthorizationRequired
       403 -> Rejected(ML_FORBIDDEN)
       429, 5xx, timeout, circuito aberto -> Rejected(ML_UNAVAILABLE); descrição NotAttempted(ML_UNAVAILABLE)
  4. Descrição (texto puro):
       existia -> PUT /items/{id}/description?api_version=2  { "plain_text": "..." }
       não existia -> POST /items/{id}/description           { "plain_text": "..." }
       mesmos tratamentos de 400/409/401/403/indisponível

Mapeamento de erro do ML ({ message, error, status, cause: [{ code, message }] }):
  field_not_updatable / "title is not modifiable"     -> FIELD_NOT_UPDATABLE
  item.description.type.invalid                       -> DESCRIPTION_INVALID
  cause/mensagem de status do item (não ativo)        -> ITEM_NOT_ACTIVE
  409 optimistic locking                              -> ML_CONFLICT
  403                                                 -> ML_FORBIDDEN
  outro 400                                           -> ML_REJECTED (Message = message do ML, sem dados sensíveis)
  (códigos exatos do ML não verificados: as fixtures são sintéticas e a tabela é conferida no go-live)

Métrica: vitrine.mercadolivre.listing_edits{field=title|description|attributes, result=applied|rejected|not_attempted, code}
Log: Warning MissingWriteScope na troca do code quando "scope" não contém "write".
DI: IMercadoLivreListingDetails e IMercadoLivreListingEditor Scoped; AlwaysUseServiceLocationFor nos dois.
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches`.
- `PublicApi`: `IMercadoLivreListingDetails.cs`, `IMercadoLivreListingEditor.cs`, `ListingChangeRules.cs`, `MercadoLivreExceptions.cs` (nova exceção).
- `Infrastructure/Api`: `MercadoLivreListingDetailsGateway.cs`, `MercadoLivreListingEditor.cs`, `MercadoLivreErrorMapper.cs`; `MercadoLivreModule.cs` (registro e `DisableForUnsafeHttpMethods`); `OAuth/MercadoLivreOAuthClient.cs` (aviso do escopo); `Diagnostics/MercadoLivreMetrics.cs`.
- Fixtures em `tests/Fixtures/MercadoLivre/`: `item-details.json`, `item-description.json`, `category.json`, `category-attributes.json`, `item-update-request.json`, `item-description-request.json`, `error-title-not-modifiable.json`, `error-description-invalid.json`, `error-item-not-active.json`, `error-conflict.json`.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Ler detalhe | item do vendedor, com descrição | `MercadoLivreItemDetails` com atributos e descrição | IT-01, CT-01 |
| Detalhe sem descrição | `/description` 404 | `Description = null` | IT-01 |
| Item inexistente ou alheio | 404 ou `seller_id` diferente | `null`; o editor lança `ItemNotFound` sem escrever | IT-01 |
| Ler categoria | `/categories/{id}` e `/attributes` | limites, tags, valores e unidades mapeados | IT-02, CT-01 |
| Cache de categoria | 2ª leitura em < 24 h; leitura após 24 h; 404 | sem chamada; nova chamada; 404 não fica em cache | IT-02 |
| Título travado | vendas, catálogo ou user product | `NotAttempted(TITLE_LOCKED_*)`; PUT só com atributos | UT-01, IT-04 |
| Título inválido | vazio ou acima do limite | `NotAttempted(TITLE_EMPTY / TITLE_TOO_LONG)` | UT-01 |
| Descrição inválida | HTML, entidade, emoji, contato, acima do limite | `NotAttempted(DESCRIPTION_*)`, sem chamar o ML | UT-02, IT-04 |
| Atributo inválido | desconhecido, `read_only`/`hidden`, valor ou unidade fora da lista | `NotAttempted(ATTRIBUTE_*)`; não vai no corpo | UT-03, IT-04 |
| Item não ativo | `status != active` | todos os campos `NotAttempted(ITEM_NOT_ACTIVE)`, zero escritas | UT-01, IT-04 |
| Publicar tudo | campos válidos, descrição existente ou não | 1 PUT `/items`, depois PUT `?api_version=2` ou POST da descrição; tudo `Applied` | IT-03, CT-02 |
| ML recusa o título | 400 título não modificável | título `Rejected(FIELD_NOT_UPDATABLE)`; reenvio só com atributos `Applied` | UT-04, IT-05 |
| ML recusa a descrição | 400 `item.description.type.invalid` | `Rejected(DESCRIPTION_INVALID)` | UT-04, IT-05 |
| Sem permissão | 403 na escrita | `Rejected(ML_FORBIDDEN)` | UT-04, IT-05 |
| Conflito | 409 uma vez / duas vezes | reenvio após ≥ 2 s → `Applied` / `Rejected(ML_CONFLICT)` | IT-06 |
| ML indisponível na escrita | 503 no PUT | 1 só PUT; campos `Rejected(ML_UNAVAILABLE)`; descrição `NotAttempted` | IT-07 |
| ML indisponível na leitura | 503 persistente no GET do item | `Unavailable` lançada; nenhuma escrita | IT-07 |
| Token rejeitado | 401 no PUT | 1 renovação forçada e 1 reenvio | IT-07 |
| Leituras inalteradas | busca e bulk da SPEC-0021 | retry de GET como antes | CH-01 |
| Escopo `write` ausente | `scope` sem `write` na troca do code | log Warning `MissingWriteScope` | IT-08 |
| Segredos e fronteiras | logs, exceções, `PublicApi` | sem token; `PublicApi` só com BCL | UT-05, IT-07 |
| Observabilidade | qualquer escrita | `listing_edits{field,result,code}` e span com caminho modelo | IT-03 |

## 7. Artefato B — Plano de Testes (TDD)
<!-- Todo teste tem um ID e é escrito ANTES da implementação (Red). No código, cada teste carrega a tag `SPEC-0033:<ID>` — o G1 (`spec_graph.py verify`) verifica. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem testes: "N/A — motivo" SEM ID. Teste que protege comportamento existente e passa antes da mudança: marque `(guarda: passa antes da mudança)`. Estratégia completa: references/testing.md da skill. -->

### 7.1 Testes de Caracterização
- **CH-01** — Dado o cliente `mercadolivre-api` com `DisableForUnsafeHttpMethods`, quando os testes de resiliência de leitura da SPEC-0021 (IT 04: 429 com `Retry-After`, 503 persistente com 3 tentativas e circuito aberto) rodam, então continuam passando sem alteração (guarda: passa antes da mudança).

### 7.2 Testes Unitários
- **UT-01** — Dado `ListingChangeRules` e itens sintéticos, quando o título é avaliado, então:
  - `sold_quantity > 0` dá `TITLE_LOCKED_SOLD`; `catalog_listing` dá `TITLE_LOCKED_CATALOG`; `user_product_id` dá `TITLE_LOCKED_USER_PRODUCT` (nessa ordem de precedência);
  - título vazio ou só espaços dá `TITLE_EMPTY`; acima de `MaxTitleLength` dá `TITLE_TOO_LONG`; no limite exato é aceito;
  - item com `status` diferente de `active` dá `ITEM_NOT_ACTIVE` na chave `item`.
- **UT-02** — Dado descrições, quando avaliadas, então:
  - `<b>`, `&nbsp;`, `&#39;` e emoji (inclusive com seletor de variação) dão `DESCRIPTION_INVALID`;
  - e-mail, telefone brasileiro (com e sem DDD e máscara), URL e a palavra "whatsapp" dão `DESCRIPTION_CONTACT`;
  - (Emenda v1.1) **não** são contato: faixa de anos ("Compatível com Gol 2008-2012"), resolução ("1920 1080"), "Lote 2024.2025", "Kit 1000 2000 W", código de peça ("Ref. 0986 4945", "Peça 12345678") e "novo.Com garantia". Telefone sem DDD só conta como celular de 9 dígitos começando por 9 (`9XXXX-XXXX` ou `9XXXXXXXX`) ou, com 8 dígitos e máscara `XXXX-XXXX`, quando há palavra de contato até 20 caracteres antes (tel, fone, telefone, celular, cel, ligue, contato, whats, zap); TLD de URL casa só em minúsculas; (Emenda v1.2) também **não** são contato: "Para Celta 2008-2012", "Tela para modelos 2018-2020", "Capa celular modelos 2019-2021", "Fone de ouvido linha 2020-2021" e "Pastilha Civic G10 2016-2021" — palavras de contato só como palavra inteira, número não pode vir colado a letra ou dígito, e faixa de anos (`19xx`/`20xx` + separador + `19xx`/`20xx`) nunca é telefone; com palavra de contato, o separador dos 8 dígitos pode ser hífen, espaço ou ponto ("tel: 3456 7890" e "Ligue 3456 7890" são contato);
  - acima de `MaxDescriptionLength` (ou de 50.000 quando a categoria não informa) dá `DESCRIPTION_TOO_LONG`;
  - texto com acentos e quebras `\n` é aceito.
- **UT-03** — Dado uma categoria com atributos livre, lista, `number_unit`, `read_only` e `hidden`, quando atributos são avaliados e normalizados, então:
  - id fora da categoria dá `ATTRIBUTE_UNKNOWN`; `read_only` ou `hidden` dá `ATTRIBUTE_NOT_EDITABLE`;
  - valor fora de `Values`, unidade fora de `AllowedUnits`, nem `ValueId` nem `ValueName`, ou texto livre com HTML ou acima de 255 dá `ATTRIBUTE_VALUE_NOT_ALLOWED`;
  - `ValueName` "preto" numa lista com "Preto" vira o `ValueId` correspondente.
- **UT-04** — Dado as fixtures de erro do ML, quando o mapeador as traduz, então: título não modificável vira `FIELD_NOT_UPDATABLE`; (Emenda v1.1) `field_not_updatable` cuja cause/mensagem não menciona `title` é recusa do corpo inteiro (`ML_REJECTED`), sem separar o título nem fazer o 2º PUT; `item.description.type.invalid` vira `DESCRIPTION_INVALID`; status não ativo vira `ITEM_NOT_ACTIVE`; 409 vira `ML_CONFLICT`; 403 vira `ML_FORBIDDEN`; outro 400 vira `ML_REJECTED` com a `message` do ML; corpo ilegível vira `ML_REJECTED` com mensagem genérica.
- **UT-05** — Dado os assemblies, quando as regras de arquitetura rodam, então `MercadoLivre.PublicApi` continua referenciando só a BCL, nenhum tipo público novo tem membro com "token" ou "secret", e os gateways novos são `internal`.

### 7.3 Testes de Integração
<!-- Postgres real + WireMock no lugar de api.mercadolibre.com, como na SPEC-0021 (suporte MercadoLivreGateway dos testes). -->
- **IT-01** — Dado uma conta com token válido e o WireMock com `GET /items/{id}` e `GET /items/{id}/description` nas fixtures, quando `GetItemDetailsAsync` é chamado, então devolve todos os campos mapeados, com `Authorization: Bearer`. Com a descrição 404, `Description` é `null`. Com o item 404 ou com `seller_id` de outro vendedor, devolve `null`, e `ApplyAsync` lança `MercadoLivreItemNotFoundException` sem nenhum PUT ou POST.
- **IT-02** — Dado as fixtures de categoria e atributos, quando `GetCategoryAsync` é chamado duas vezes, então o WireMock recebe 1 chamada a cada endpoint, e o resultado tem limites, `Required` (inclusive `catalog_required`), `ReadOnly`, `Hidden`, valores e unidades. Depois de 24 h no relógio controlado do cache, há nova chamada. Uma categoria 404 devolve `null` e é consultada de novo na chamada seguinte.
- **IT-03** — Dado um item ativo sem vendas, com descrição existente, quando `ApplyAsync` recebe título, descrição e dois atributos válidos, então:
  - há exatamente 1 `PUT /items/{id}` com `title` e `attributes` (`value_id` para lista, `value_name` para livre) igual a `item-update-request.json`, e depois 1 `PUT /items/{id}/description?api_version=2` com `plain_text`;
  - com o item sem descrição, a descrição vai por `POST /items/{id}/description`;
  - todos os campos voltam `Applied`;
  - `vitrine.mercadolivre.listing_edits{field,result=applied}` é incrementada e os spans têm o caminho modelo, sem o id.
- **IT-04** — Dado pré-condições locais violadas, quando `ApplyAsync` é chamado, então:
  - item com vendas: título `NotAttempted(TITLE_LOCKED_SOLD)` e PUT só com os atributos;
  - item `paused`: todos os campos `NotAttempted(ITEM_NOT_ACTIVE)` e nenhuma escrita;
  - descrição com HTML: `NotAttempted(DESCRIPTION_INVALID)` e nenhuma chamada de descrição;
  - atributo `read_only` e valor fora da lista: `NotAttempted(ATTRIBUTE_*)`, fora do corpo do PUT.
- **IT-05** — Dado o ML recusando, quando `ApplyAsync` é chamado, então:
  - PUT com título e atributos respondido com 400 de título não modificável: título `Rejected(FIELD_NOT_UPDATABLE)`, um 2º PUT só com os atributos e atributos `Applied`;
  - descrição respondida com 400 `item.description.type.invalid`: `Rejected(DESCRIPTION_INVALID)`;
  - 403: `Rejected(ML_FORBIDDEN)`.
- **IT-06** — Dado o PUT respondendo 409 e depois 200, quando `ApplyAsync` é chamado, então há exatamente 2 PUTs, com intervalo ≥ 2 s (relógio de teste), e o campo fica `Applied`. Com 409 duas vezes, há 2 PUTs e o campo fica `Rejected(ML_CONFLICT)`.
- **IT-07** — Dado falhas, quando `ApplyAsync` é chamado, então:
  - PUT com 503: exatamente 1 PUT (sem retry do pipeline), campos `Rejected(ML_UNAVAILABLE)` e descrição `NotAttempted(ML_UNAVAILABLE)`;
  - `GET /items/{id}` com 503 persistente: `MercadoLivreUnavailableException` depois das 3 tentativas de leitura e nenhuma escrita;
  - PUT com 401 uma vez: 1 renovação forçada e 1 reenvio que conclui;
  - os logs capturados e as mensagens das exceções não contêm access token nem refresh token.
- **IT-08** — Dado a troca do code com `scope: "offline_access read"`, quando a conexão é concluída, então sai um log Warning `MissingWriteScope`, sem dados do token, e a conexão segue como na SPEC-0020. Com `write` no escopo, não há aviso.

### 7.4 Testes de Contrato
- **CT-01** — Dado as fixtures de resposta em `tests/Fixtures/MercadoLivre/` (`item-details`, `item-description`, `category`, `category-attributes` e os erros), com a origem anotada (sintéticas, a partir dos trechos de busca de 2026-11-02), quando mapeadas pelo gateway, então produzem os tipos do §6 com todos os campos. O simulador (SPEC-0034) compara suas respostas com as mesmas fixtures, e o `Listings` (SPEC-0035/0036) monta seus dublês a partir delas.
- **CT-02** — Dado as fixtures de requisição (`item-update-request.json`, `item-description-request.json`), quando o editor monta os corpos do PUT e do POST, então são iguais às fixtures (campos e tipos). A SPEC-0034 valida que o simulador aceita esses corpos.

### 7.5 Testes E2E
- N/A — sem jornada própria nem tela. A jornada `aprovar-melhoria` passa por este gateway contra o simulador na SPEC-0038 (E2E 01, 02 e 03).

### 7.6 Outros
- N/A.

**Dublês e dados de teste:**
- WireMock com cenários de estado (`InScenario`) para 409→200, 401→200 e o 400 do título seguido do 200 só com atributos;
- builder de conta com tokens cifrados (suporte `MercadoLivreGateway` da SPEC-0021);
- `TimeProvider` falso para o intervalo do 409 e relógio controlado do cache;
- IDs `MLB` e categorias sintéticas (`MLB90001`…).

**Ambiente de execução:** UT em processo; IT com Testcontainers e WireMock (local e CI).

## 8. Plano de Rollout
- **Estratégia:** deploy direto. Os contratos só são usados pelas SPEC-0035/0036; até lá não há chamada nova ao ML.
- **Dados/schema:** sem migração.
- **Compatibilidade:** `IMercadoLivreItems` v1 inalterado. Contratos novos em v1. `DisableForUnsafeHttpMethods` não muda nenhuma chamada existente (todas GET; o `POST /oauth/token` usa outro cliente).
- **Observabilidade:**
  - `vitrine.mercadolivre.listing_edits{field,result,code}`;
  - spans `Vitrine.MercadoLivre` com `ml.endpoint` modelo e `ml.account_id`;
  - log Information por escrita aplicada (item, campos, sem conteúdo) e Warning por recusa (código);
  - Warning `MissingWriteScope`.
- **Rollback:** revert do merge.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Repetir PUT/POST em falha transitória? — Não. Só o 409 (uma vez, depois de 2 s, como o ML orienta) e o reenvio depois de um 401 com renovação, que garante que nada foi aplicado. Timeout de escrita volta como `ML_UNAVAILABLE`, e o chamador confirma relendo o anúncio (SPEC-0036). (Architect, 2026-11-02, a confirmar no H1)
- [x] Onde fica a regra que a IA e o vendedor precisam respeitar? — Em `ListingChangeRules`, pura, no `PublicApi`, para o editor e o `Listings` usarem exatamente a mesma regra. (Architect, 2026-11-02, a confirmar no H1)
- [x] Como garantir o escopo `write`? — O ML não recebe escopo na URL de autorização; ele vem do app no DevCenter (SPEC-0020, `offline_access read write`). O Vitrine avisa no log quando o token chega sem `write` e traduz o 403 da escrita para `ML_FORBIDDEN`. Conferir no go-live (SPEC-0009). (Architect, 2026-11-02, a confirmar no H1)
- [x] Os códigos de erro do ML estão confirmados? — Não. Os fatos foram verificados só por trechos de busca em 2026-11-02 (a documentação oficial respondeu 403). As fixtures são sintéticas, e a tabela de mapeamento é conferida com usuário de teste real no go-live (SPEC-0009). (Architect, 2026-11-02, a confirmar no H1)

**Referências (pesquisa de 2026-11-02, só trechos de busca; a documentação oficial devolveu 403. Precisam ser confirmadas com usuário de teste real no go-live, SPEC-0009):**
- Título: `PUT /items/{id}` com `title`; com `sold_quantity > 0` o título não é modificável (400 provável `field_not_updatable`, "title is not modifiable"; código exato não verificado); em user products o título vem do `family_name` (`PUT /items/{id}/family_name`, ≤ 120 caracteres); em catálogo (`catalog_listing=true`) não é editável (inferido); limite em `settings.max_title_length` da categoria.
- Descrição: `POST /items/{id}/description` quando não existe; `PUT /items/{id}/description?api_version=2` com `plain_text` quando existe; só texto puro com `\n`; HTML, entidades e emojis dão 400 `item.description.type.invalid`; limite em `settings.max_description_length` (Global Selling: 50.000); sem dados de contato.
- Atributos: `GET /categories/{id}/attributes` com `tags` (`required`, `catalog_required`, `hidden`, `read_only`), `value_type`, `values`, `allowed_units`; escrita por `PUT /items/{id}` com `attributes: [{id, value_id | value_name}]`; não enviar `read_only` nem `hidden`.
- Erros: só item `active` é editável; 409 "optimistic locking" pede nova tentativa depois de alguns segundos; 429 sem limite publicado; escopo `write` necessário.
- Notificação `items` dispara em qualquer mudança, inclusive (inferido) no PUT do próprio app.

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [ ] CH-01 escrito, passando no código atual (leitura atual de anúncios pelo gateway), commitado antes de qualquer mudança

**Fase 1: Regras de edição**
- [ ] Red: escrever UT-01..UT-05 com a tag `SPEC-0033:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `ListingChangeRules` e mapeamento de erros do ML para os códigos do contrato
- [ ] Refactor mantendo tudo verde

**Fase 2: Gateway de leitura e escrita**
- [ ] Red: escrever IT-01..IT-08 e CT-01..CT-02 com a tag `SPEC-0033:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `IMercadoLivreListingDetails` (detalhes + categoria com cache de 24 h), `IMercadoLivreListingEditor.ApplyAsync` com resultado por campo e `DisableForUnsafeHttpMethods` no cliente `mercadolivre-api`
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
| G0 Spec | PASS | validate: 0 erro(s) — b44124e (árvore suja) | 2026-11-02 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 1669585 | 2026-11-02 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (at least ~694ms faster with isolate: false — reuses workers across files instead of one pe); lint exit 0 ([0m[34mChecked 67 files in 46[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 93%) — c5284b5 | 2026-11-02 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 2s 132ms) — c5284b5 | 2026-11-02 |
| G4 Review | FAIL | Reviewer 2ª rodada: CHANGES_REQUESTED — falsos positivos residuais de telefone (Celta/Tela casam cel/tel sem \b; 'G10 2016-2021' vira DDD + faixa de anos) | 2026-11-02 |
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0033`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (contrato inalterado) | 2026-11-02 | (1) UT-02: heurística de telefone sem DDD restrita (celular de 9 dígitos começando por 9, ou 8 dígitos com máscara e palavra de contato próxima) e casos negativos (faixa de anos, resolução, códigos de peça, "novo.Com"); TLD de URL só em minúsculas; (2) UT-04: `field_not_updatable` só culpa o título quando a cause/mensagem cita `title`, senão `ML_REJECTED` para o PUT inteiro; (3) `ApplyAsync` com `AttributeId` repetido lança `ArgumentException` antes de qualquer escrita; (4) quando uma exceção (2º 401, cancelamento) interrompe depois de escritas concluídas, os campos já aplicados são registrados (log + `listing_edits`) antes de propagar; (5) decisões registradas: categoria 404 no `ApplyAsync` = `MercadoLivreUnavailableException` sem escrita; `listing_edits` emite uma medição por par (resultado, código) distinto entre os atributos | G4 (Reviewer): falsos positivos de contato bloqueariam anúncios reais de autopeças e eletrônicos; motivo errado ao vendedor; robustez | SPEC-0035, SPEC-0036 (usam `ListingChangeRules`; contrato igual) | thomas (piloto automático do épico SPEC-0032) |
| v1.2 (contrato inalterado) | 2026-11-02 | UT-02: palavras de contato com limite de palavra, número sem letra/dígito colado antes, faixa de anos excluída nos ramos com DDD e com palavra de contato, separador `[ .-]` com palavra de contato; 5 casos negativos e 2 positivos novos | G4 2ª rodada: "Celta"/"Tela" casavam `cel`/`tel` e "G10 2016-2021" virava DDD + número | SPEC-0035, SPEC-0036 (mesma regra; contrato igual) | thomas (piloto automático do épico SPEC-0032) |

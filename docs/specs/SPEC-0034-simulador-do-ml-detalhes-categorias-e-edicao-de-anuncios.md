---
id: SPEC-0034
title: "Simulador do ML: detalhes, categorias e edição de anúncios"
tier: full
type: feature
user_facing: false
status: implemented
created: 2026-11-02
parent: SPEC-0032
depends_on: [SPEC-0024, SPEC-0033]
consumes_contract: [SPEC-0033@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [tools/MercadoLivreSimulator/**, tests/Vitrine.UnitTests/MercadoLivreSimulator/**, tests/Vitrine.IntegrationTests/MercadoLivreSimulator/**]
adrs: [ADR-0016]
external: []
size: M
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0034 — Simulador do ML: detalhes, categorias e edição de anúncios

<!-- type: feature | fix | refactor | migration | foundation. user_facing: true quando a mudança altera uma jornada do usuário (UI ou API pública) — exige teste E2E. Substitua todos os marcadores com chaves duplas: o G0 (`spec_graph.py validate`) reprova a spec enquanto restar algum. As seções de Checklist em diante são preenchidas depois da aprovação, sem marcadores. -->

## 1. Visão Geral
O simulador do Mercado Livre (SPEC-0024, ADR-0016) passa a servir o que a melhoria de anúncios usa:
- o detalhe do anúncio (`GET /items/{id}`, com categoria, atributos, vendas e catálogo);
- a descrição (`GET`, `POST` e `PUT /items/{id}/description`);
- as categorias (`GET /categories/{id}` e `/categories/{id}/attributes`), com 3 categorias sintéticas;
- a edição (`PUT /items/{id}`), com as regras do ML: título bloqueado com vendas ou catálogo, atributo `read_only`, valor fora da lista, item não ativo;
- a notificação `items` depois de cada edição, como o ML real faz.

Ganha também controles `/__sim/*` para preparar cenários (vendas, catálogo, descrição, falha forçada na próxima escrita) e uma massa inicial com descrições e atributos incompletos. Assim, a jornada `aprovar-melhoria` roda inteira no `aspire run`, no E2E e no ambiente local, sem conta no ML.

## 2. Motivação & Escopo
**Motivação:** o ML não tem sandbox (ADR-0016). Sem o simulador, a jornada de aprovar e publicar uma melhoria só poderia ser testada contra contas reais, com risco de alterar anúncios de verdade. O E2E da SPEC-0038 precisa de um ML com estado, que aceite e recuse edições como o real.

**Objetivos (dentro do escopo):**
- Endpoints do ML no contrato da SPEC-0033@1 (§6), no formato das fixtures de `tests/Fixtures/MercadoLivre/`.
- Regras de edição do ML aplicadas pelo simulador, com corpo de erro no formato do ML (`{ message, error, status, cause }`); a edição é atômica (qualquer violação: nada muda).
- Notificação `items` para `Simulator:NotificationUrl` depois de cada escrita aceita, reaproveitando o envio do `/__sim/notify`.
- Massa inicial nova, por vendedor (inclusive os criados por `POST /__sim/sellers`):
  - cada anúncio tem categoria sintética, atributos com ao menos 1 obrigatório faltando e, em metade dos anúncios, descrição em texto puro com dados que permitem derivar o atributo faltante ("Cor: Preto");
  - o 1º anúncio ativo tem vendas (`sold_quantity` 12) e o 2º é de catálogo (`catalog_listing` true); os demais ativos ficam sem vendas;
  - anúncios pausados e encerrados mantêm as vendas atuais (`índice % 37`).
- Controles `/__sim/*`: alterar vendas, catálogo, descrição e atributos; forçar a resposta da próxima escrita (409, 500 ou 400 com erro escolhido); listar as escritas recebidas por anúncio.
- IDs e reinício seguem a SPEC-0031 (IDs de vendedor únicos por execução; a massa volta no `reset`).

**Não-objetivos (fora do escopo):**
- Nota de qualidade (`/item/{id}/performance`), `family_name` de user products, fotos, preço e variações.
- Fidelidade além do contrato da SPEC-0033: limites de taxa reais e erros raros.
- Persistência: o estado continua em memória.
- Uso em staging ou produção: proibido (ADR-0016).

## 3. Dependências
- **Implementações necessárias:**
  - SPEC-0024, porque esta spec estende o simulador, seu estado e o envio de notificações;
  - SPEC-0033, porque os CT comparam o simulador com as fixtures de requisição e resposta que a SPEC-0033 versiona em `tests/Fixtures/MercadoLivre/`.
- **Contratos consumidos:** SPEC-0033@1, que define os endpoints, os corpos de requisição e as respostas que o gateway usa, com as fixtures de `tests/Fixtures/MercadoLivre/`. O simulador implementa o mesmo formato.
- **Pré-requisitos externos:** N/A.

## 4. Decisão Arquitetural
**Contexto:** segue o simulador da SPEC-0024:
- `tools/MercadoLivreSimulator/MlEndpoints.cs` (endpoints do ML, `ApiError` no formato do ML);
- `SimulatorState.cs` (estado em memória sob um lock, massa em `AddItemsLocked`, `Reset`);
- `ControlEndpoints.cs` (`/__sim/*`, envio da notificação pelo cliente `NotificationClient`);
- `MlContracts.cs` (JSON em `snake_case`);
- testes em `tests/Vitrine.IntegrationTests/MercadoLivreSimulator/` (`WebApplicationFactory`, `SimulatorClient`).

**Decisão:**
- Catálogo de categorias estático em código (`SimulatorCategories.cs`), com 3 categorias.
- Regras de edição numa classe interna pura (`ItemEditRules`), testável em unidade, com os mesmos casos da `ListingChangeRules` da SPEC-0033, mas implementada à parte: o simulador imita o ML, não o Vitrine.
- Falha forçada guardada no estado do anúncio (`PendingWriteFailure`, por alvo `item` ou `description`), consumida pela próxima escrita.
- Notificação disparada fora do lock, sem esperar a entrega (falha só gera log).

**Justificativa:** mantém o simulador como uma ferramenta pequena, de estado em memória (ADR-0016). Implementar a regra à parte da `ListingChangeRules` faz o E2E pegar divergências entre o Vitrine e o "ML", em vez de esconder as duas atrás do mesmo código.

**Desvio do padrão existente:** nenhum.

**Alternativas descartadas:**
- Reaproveitar `ListingChangeRules` do `PublicApi` no simulador: a Api e o simulador ficariam acoplados (SPEC-0024, UT-04), e o simulador deixaria de ser um oráculo independente.
- WireMock com mapeamentos no E2E: sem estado nem notificação (ADR-0016).

**ADRs:** ADR-0016.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** N/A — ferramenta de teste, em memória.
- **Segurança:**
  - `PUT`/`POST` exigem Bearer do dono do anúncio: sem token 401, token de outro vendedor 403 — IT-02;
  - continua fora da imagem da Api e do Compose de produção (testes da SPEC-0024 seguem como guarda — CH-01).
- **Privacidade e dados pessoais:** N/A — só dados sintéticos.
- **Disponibilidade e resiliência:** N/A — sem SLA. A notificação é enviada sem bloquear a resposta da escrita.
- **Acessibilidade (UI):** N/A — sem tela nova.
- **Custo:** N/A.

## 6. Artefato A — Contrato
**Interface:** aplicação `ml-simulator` (mesma porta e configuração da SPEC-0024).

```text
Formato conforme o contrato da SPEC-0033@1 e as fixtures de tests/Fixtures/MercadoLivre/ (sintéticas, 2026-11-02).
Erros da API: { "message", "error", "status", "cause": [{ "code", "message" }] }.

GET  /items/{id}[?attributes=campos]      token opcional (recurso público); token inválido -> 401; inexistente -> 404
     200 { id, seller_id, category_id, title, price, currency_id, available_quantity, sold_quantity, status,
           catalog_listing, user_product_id: null, permalink, thumbnail, secure_thumbnail, listing_type_id,
           last_updated, attributes: [{ id, name, value_id, value_name }] }
GET  /items/{id}/description              200 { plain_text, last_updated, date_created } | 404 sem descrição
POST /items/{id}/description              { plain_text } -> 201; já existe -> 400 (comportamento do ML não verificado:
                                          o gateway só usa POST quando a leitura não achou descrição)
PUT  /items/{id}/description?api_version=2 { plain_text } -> 200; sem descrição -> 404
     validação da descrição: tag ou entidade HTML, emoji -> 400 item.description.type.invalid;
                             acima de max_description_length -> 400 item.description.max_length (sintético)
PUT  /items/{id}                          { title?, attributes?: [{ id, value_id? | value_name? }] }
     sem token 401; token de outro vendedor 403; atômico (qualquer violação: nada muda)
     status != active                     -> 400 cause item.status.not_active (sintético; Emenda v1.1)
     title com sold_quantity > 0 ou catalog_listing -> 400 field_not_updatable, "title is not modifiable"
     title vazio ou > max_title_length    -> 400 item.title.length.invalid (sintético)
     atributo desconhecido, read_only ou hidden, value_id fora de values, unidade fora de allowed_units
                                          -> 400 item.attributes.invalid (sintético), cause com o id
     200 -> item atualizado (last_updated = agora) e notificação items enviada
     toda escrita aceita (item ou descrição) envia { topic: "items", resource: "/items/{id}", user_id, ... }
     para Simulator:NotificationUrl, no formato do /__sim/notify
GET  /categories/{id}                     200 { id, name, settings: { max_title_length, max_description_length } } | 404
GET  /categories/{id}/attributes          200 [{ id, name, value_type, tags: { required?, catalog_required?, read_only?,
                                          hidden? }, values: [{ id, name }], allowed_units: [{ id, name }] }] | 404

Categorias sintéticas (max_title_length 60, max_description_length 50000):
  MLB90001 "Eletrônicos e acessórios (simulado)": fone, capa, luminária, teclado, carregador, mouse
  MLB90002 "Casa e cozinha (simulado)": garrafa, caneca, organizador
  MLB90003 "Bolsas e mochilas (simulado)": mochila
  atributos comuns: BRAND (string, required), MODEL (string), COLOR (list, required: Preto, Branco, Azul, Vermelho),
                    ITEM_CONDITION (list, read_only: Novo), PRODUCT_DATA_SOURCE (string, hidden)
  MLB90001: IS_WIRELESS (boolean: Sim, Não)
  MLB90002: CAPACITY (number_unit, unidades mL e L), MATERIAL (list: Aço inoxidável, Cerâmica, Plástico)
  MLB90003: LAPTOP_SIZE (number_unit, unidade pol), MATERIAL (list: Poliéster, Nylon, Couro sintético)

Massa por vendedor (reset e /__sim/sellers):
  todo anúncio: BRAND "Marca Simulada" e ITEM_CONDITION "Novo" preenchidos; COLOR e MODEL vazios
  anúncios de número par: descrição "{produto} simulado. Cor: Preto. Marca: Marca Simulada. Anúncio de teste."
  anúncios de número ímpar: sem descrição
  1º ativo: sold_quantity 12; 2º ativo: catalog_listing true, sold_quantity 0; demais ativos: sold_quantity 0
  pausados e encerrados: sold_quantity = índice % 37 (como hoje)

Controle (só dev/E2E):
POST /__sim/sellers/{id}/items/{itemId}   body passa a aceitar também catalogListing?, description? (string; "" apaga),
                                          attributes? ([{ id, value_id?, value_name? }], substitui a lista)
POST /__sim/sellers/{id}/items/{itemId}/fail-next-write
     body { target: "item" | "description", status: 400 | 409 | 500, error?, message?, causeCode?, times? (padrão 1) }
     -> 204; as próximas `times` escritas no alvo respondem assim, sem mudar nada
GET  /__sim/sellers/{id}/items/{itemId}/writes  -> 200 [{ method, path, body, status, at }] (escritas recebidas)
POST /__sim/reset                          também limpa falhas forçadas e o registro de escritas
```

**Design:** N/A — sem tela nova.

**Arquivos/módulos afetados:** ver `touches`. `MlEndpoints.cs`, `SimulatorState.cs`, `ControlEndpoints.cs`, `MlContracts.cs` e os novos `SimulatorCategories.cs` e `ItemEditRules.cs`, mais os testes.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Detalhe do anúncio | `GET /items/{id}` | campos e atributos no formato da fixture; 404 se não existe | IT-01, CT-01 |
| Descrição | `GET` com e sem descrição | 200 com `plain_text` ou 404 | IT-01, CT-01 |
| Categorias | `GET /categories/{id}` e `/attributes` | limites, tags, valores e unidades; 404 se não existe | IT-01, CT-01 |
| Editar título e atributos | `PUT /items/{id}` válido | 200; item atualizado; `last_updated` novo; bulk reflete | IT-02, CT-02 |
| Título com vendas ou catálogo | `PUT` com `title` | 400 `field_not_updatable`; nada muda | UT-01, IT-02 |
| Atributo inválido | `read_only`, `hidden`, desconhecido, fora da lista ou da unidade | 400 `item.attributes.invalid`; nada muda | UT-01, IT-02 |
| Item não ativo | `PUT` em pausado | 400 de status; nada muda | UT-01, IT-02 |
| Sem permissão | sem token / outro vendedor | 401 / 403 | IT-02 |
| Criar e atualizar descrição | `POST` sem descrição, `PUT ?api_version=2` com descrição | 201 / 200; `PUT` sem descrição dá 404 | IT-03, CT-02 |
| Descrição inválida | HTML, entidade, emoji, acima do limite | 400 `item.description.type.invalid` / limite | UT-02, IT-03 |
| Notificação após edição | escrita aceita | POST `items` entregue à `NotificationUrl` | IT-04 |
| Massa inicial | `reset` e `/__sim/sellers` | 1 com vendas, 1 de catálogo, obrigatório faltando, descrição derivável | UT-03, IT-05 |
| Preparar cenário | `/__sim/.../items/{itemId}` com vendas, catálogo, descrição, atributos | estado alterado | IT-05 |
| Falha forçada | `fail-next-write` 409, 500 ou 400 | próxima escrita responde assim, sem mudar nada; a seguinte é normal | IT-05 |
| Registro de escritas | `GET .../writes` | escritas recebidas na ordem | IT-05 |
| Comportamento anterior | testes da SPEC-0024 e da SPEC-0031 | continuam passando | CH-01 |

## 7. Artefato B — Plano de Testes (TDD)
<!-- Todo teste tem um ID e é escrito ANTES da implementação (Red). No código, cada teste carrega a tag `SPEC-0034:<ID>` — o G1 (`spec_graph.py verify`) verifica. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem testes: "N/A — motivo" SEM ID. Teste que protege comportamento existente e passa antes da mudança: marque `(guarda: passa antes da mudança)`. Estratégia completa: references/testing.md da skill. -->

### 7.1 Testes de Caracterização
- **CH-01** — Dado os testes atuais do simulador (OAuth, busca, bulk, notificação, isolamento de deploy e IDs após reinício, das SPEC-0024 e SPEC-0031), quando rodam depois da mudança da massa e dos endpoints novos, então continuam passando (guarda: passa antes da mudança).

### 7.2 Testes Unitários
- **UT-01** — Dado `ItemEditRules` e anúncios sintéticos, quando um `PUT /items/{id}` é avaliado, então:
  - título com vendas ou de catálogo dá `field_not_updatable`; título vazio ou acima de 60 dá o erro de tamanho;
  - atributo `read_only`, `hidden`, desconhecido, `value_id` fora da lista ou unidade fora de `allowed_units` dá `item.attributes.invalid` com o id na `cause`;
  - item pausado dá o erro de status;
  - com qualquer violação, o anúncio não muda (atomicidade).
- **UT-02** — Dado descrições, quando validadas, então tag HTML, entidade HTML e emoji dão `item.description.type.invalid`; acima de 50.000 dá o erro de tamanho; texto com acentos e `\n` é aceito.
- **UT-03** — Dado o estado depois do `reset` e um vendedor criado por `/__sim/sellers`, quando a massa é inspecionada, então em cada vendedor:
  - o 1º ativo tem `sold_quantity` 12, o 2º tem `catalog_listing` true e os demais ativos têm 0;
  - todo anúncio tem categoria existente, `BRAND` e `ITEM_CONDITION` preenchidos e `COLOR` vazio;
  - anúncios pares têm descrição com "Cor: Preto", e ímpares não têm descrição;
  - um 2º `reset` recria a mesma massa.

### 7.3 Testes de Integração
- **IT-01** — Dado o simulador em `WebApplicationFactory`, quando `GET /items/{id}`, `GET /items/{id}/description` e `GET /categories/{id}` (+ `/attributes`) são chamados, então as respostas trazem os campos do §6, com tags, valores e unidades. Item, descrição e categoria inexistentes dão 404 no formato do ML; token inválido no `GET /items/{id}` dá 401.
- **IT-02** — Dado um vendedor criado e seu token, quando `PUT /items/{id}`:
  - com título e `COLOR` válidos no 3º ativo: 200, e `GET /items/{id}` e `/items/bulk` mostram os valores novos com `last_updated` maior;
  - com título no 1º ativo (vendas) ou no 2º (catálogo): 400 `field_not_updatable`, e nada muda;
  - com `ITEM_CONDITION` (`read_only`), `PRODUCT_DATA_SOURCE` (`hidden`) ou `COLOR` com `value_id` inexistente: 400, e nada muda;
  - num anúncio pausado: 400 de status;
  - sem token: 401; com token de outro vendedor: 403.
- **IT-03** — Dado um anúncio sem descrição, quando `POST /items/{id}/description` cria e `PUT /items/{id}/description?api_version=2` atualiza, então o `GET` devolve o texto novo. `PUT` num anúncio sem descrição dá 404, e descrição com HTML ou emoji dá 400 `item.description.type.invalid`.
- **IT-04** — Dado um WireMock como receptor em `Simulator:NotificationUrl`, quando um `PUT /items/{id}` e um `PUT` de descrição são aceitos, então o receptor recebe 2 POSTs `topic=items`, `resource=/items/{id}`, com o `user_id` do vendedor. Uma escrita recusada não gera notificação, e o receptor fora do ar não muda a resposta da escrita.
- **IT-05** — Dado os controles `/__sim/*`, então:
  - `POST /__sim/sellers/{id}/items/{itemId}` com `soldQuantity`, `catalogListing`, `description` e `attributes` altera o anúncio, e `description: ""` apaga a descrição;
  - `fail-next-write` com `{ target: "item", status: 409 }` faz o próximo PUT responder 409 sem mudar nada, e o seguinte responde 200;
  - com `status: 500` e com `status: 400, causeCode: "item.description.type.invalid"` no alvo `description`, a próxima escrita de descrição responde assim;
  - `GET .../writes` lista as escritas na ordem, com método, caminho, corpo e status;
  - `POST /__sim/reset` limpa as falhas forçadas e o registro.

### 7.4 Testes de Contrato
- **CT-01** — Dado as fixtures de resposta da SPEC-0033 (`item-details`, `item-description`, `category`, `category-attributes` e os erros) em `tests/Fixtures/MercadoLivre/`, quando comparadas com as respostas do simulador, então têm os mesmos campos e tipos nos campos usados pelo Vitrine. É o lado provedor do contrato SPEC-0033@1.
- **CT-02** — Dado as fixtures de requisição da SPEC-0033 (`item-update-request.json`, `item-description-request.json`), quando enviadas ao simulador para um anúncio editável, então são aceitas (200/201) e aplicadas.

### 7.5 Testes E2E
- N/A — ferramenta de teste, sem jornada própria. É exercitada pela SPEC-0038 (E2E 01 a 05).

### 7.6 Outros
- N/A.

**Dublês e dados de teste:** massa sintética fixa; vendedores criados por teste; WireMock como receptor de notificação.

**Ambiente de execução:** UT e IT em processo (local e CI).

## 8. Plano de Rollout
- **Estratégia:** deploy direto, só em dev, CI e ambiente local. Não existe em staging nem em produção.
- **Dados/schema:** N/A — estado em memória.
- **Compatibilidade:** endpoints novos; a massa muda as vendas dos anúncios ativos (só 1 com vendas por vendedor). Nenhum teste atual depende dessas vendas (conferido em `web/e2e` e nos testes do simulador). Quem tem o ambiente local reconstrói a imagem `vitrine-ml-simulator:local` (runbook da SPEC-0024).
- **Observabilidade:** log Information por escrita recebida (anúncio, alvo, status); Warning quando a notificação não é entregue.
- **Rollback:** revert do merge.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Quantos anúncios com vendas na massa? — Só o 1º ativo de cada vendedor. Hoje quase todos têm vendas (`índice % 37`), o que bloquearia o título em quase toda a massa e esconderia o caminho feliz da jornada. Pausados e encerrados mantêm o valor atual. (Architect, 2026-11-02, a confirmar no H1)
- [x] As regras do simulador reaproveitam o código do Vitrine? — Não. O simulador imita o ML de forma independente, para o E2E pegar divergências entre os dois. (Architect, 2026-11-02, a confirmar no H1)
- [x] Os códigos de erro do simulador são os do ML? — Os que os trechos de busca de 2026-11-02 trazem (`field_not_updatable`, `item.description.type.invalid`, 409 de *optimistic locking*) sim. Os demais são sintéticos e marcados como tais; todos são conferidos com usuário de teste real no go-live (SPEC-0009). (Architect, 2026-11-02, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [x] CH-01 escrito, passando no código atual (simulador atual: busca, bulk e notificações), commitado antes de qualquer mudança

**Fase 1: Estado do simulador**
- [x] Red: escrever UT-01..UT-03 com a tag `SPEC-0034:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: categorias sintéticas MLB90001–3, descrição e regras de edição em memória
- [x] Refactor mantendo tudo verde

**Fase 2: Endpoints do simulador**
- [x] Red: escrever IT-01..IT-05 e CT-01..CT-02 com a tag `SPEC-0034:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: detalhes, descrição, `/categories/{id}/attributes`, PUT com as regras do ML, notificação após editar e `/__sim` `fail-next-write`/writes
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
| G0 Spec | PASS | validate: 0 erro(s) — b44124e (árvore suja) | 2026-11-02 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — ce7bca8 | 2026-11-02 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (learn more: https://vitest.dev/guide/improving-performance#test-environments); lint exit 0 ([0m[34mChecked 67 files in 75[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 93%) — d25bc38 | 2026-11-02 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 2s 117ms) — d25bc38 | 2026-11-02 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED_WITH_NOTES (0 blocker/major; compatível com o MercadoLivreErrorMapper da SPEC-0033; minors: catch estreito no NotificationSender, descrição aceita em item não ativo (conferir no go-live), oráculo mais permissivo que ListingChangeRules) — d25bc38 | 2026-11-02 |
| G5 Integração & CI | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (at least ~683ms faster with isolate: false — reuses workers across files instead of one pe); test_integration exit 0 (duration: 4m 34s 884ms); test_e2e exit 0 (40 passed (2.1m)); arch_test exit 0 (duration: 2s 242ms); security_scan exit 0 ([90m2:27PM[0m [32mINF[0m [1mno leaks found[0m) — 0c5b08e | 2026-11-02 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-11-02 |
| G6 Deploy | PASS | MANUAL: deploy local cde90e6 (main após PR #68) com o simulador novo (vitrine-ml-simulator:local): deploy.sh OK (health 200 em 5 s); smoke BASE_URL=https://vitrine.localhost → 4 passed; GET https://ml-simulador.vitrine.localhost/categories/MLB90001 → 200 com max_title_length 60 e max_description_length 50000 | 2026-11-02 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — fc8777a | 2026-11-02 |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
O simulador do Mercado Livre agora serve o anúncio completo (categoria, atributos, vendas, catálogo, descrição), três categorias sintéticas (MLB90001–3) com limites e atributos, e aceita editar título, atributos e descrição com as regras do ML: título travado com vendas ou catálogo, só valores permitidos, descrição em texto puro e item ativo. Depois de uma edição aceita, envia a notificação `items` como o ML. Os testes ganharam controles para forçar falhas de escrita e inspecionar as escritas recebidas. Continua só em desenvolvimento, E2E e ambiente local (ADR-0016).

### Como foi feito
- `tools/MercadoLivreSimulator`: `SimulatorCategories` (categorias e atributos sintéticos; COLOR com os ids da fixture), `ItemEditRules` (regras puras e atômicas), `SimulatorState` (massa com vendas e catálogo, `EditItem`, `WriteDescription`, `FailNextWrite`, `Writes`), `MlEndpoints` (detalhes, descrição, categorias, PUT), `ControlEndpoints` (`fail-next-write`, `writes`, atributos no controle), `NotificationSender` (envio extraído do `/__sim/notify`, disparado depois de escrita aceita).
- Emenda v1.1: item não ativo responde `item.status.not_active`, como o gateway da SPEC-0033 espera; leituras de ids e formato registradas.
- G4 aprovado com notas: `catch` estreito no `NotificationSender`; o simulador é mais permissivo que `ListingChangeRules` em alguns casos (o Vitrine valida antes).

### Prova de Correção
N/A — `type: feature`.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| CH-01 | OAuth, busca, bulk, notificações e IDs do simulador (SPEC-0024/0031) inalterados | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| UT-01 | Regras de edição do item: status, título travado com vendas/catálogo, limite, atributos de lista, `number_unit`, read_only; atomicidade | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| UT-02 | Regras de descrição: tag, entidade, emoji e limite de 50.000 | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| UT-03 | Massa: vendas, catálogo, categoria, atributos e descrição nos anúncios pares | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| IT-01 | `GET /items/{id}`, descrição, `/categories/{id}` e `/attributes` no formato do ML | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| IT-02 | `PUT /items/{id}` com as regras do ML (inclui `item.status.not_active`, Emenda v1.1) | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| IT-03 | Descrição: POST quando não existe e PUT `api_version=2` quando existe | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| IT-04 | Notificação `items` depois de uma escrita aceita, nenhuma em escrita recusada | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| IT-05 | Controles `/__sim/fail-next-write` e `/__sim/writes` | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| CT-01 | Respostas do simulador no formato das fixtures da SPEC-0033 | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |
| CT-02 | Requisições do gateway (fixtures da SPEC-0033) aceitas pelo simulador | PASS | CI PR #68 runs/41491351573 (d25bc38); main cde90e6 |

### Definição de Pronto
- [x] Todos os testes do plano passando e listados na Verificação
- [x] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [x] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [x] Requisitos não-funcionais medidos com evidência (ou N/A justificado)
- [x] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6)
- [x] Observabilidade e rollback prontos conforme o Plano de Rollout
- [x] Documentação raiz e CHANGELOG atualizados (G7)
- [x] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7)
- [x] Pendências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, `ghcr.io/thomasravache/vitrine-api:cde90e6` + `vitrine-ml-simulator:local`, 2026-11-02: smoke 4/4 e `/categories/MLB90001` respondendo pelo Caddy. E2E do CI (40) verdes com o simulador novo. Nunca vai a staging/produção.

### Pendências
Para o go-live com o usuário: conferir se o ML real aceita mudar a descrição de anúncio pausado (o simulador aceita; o Vitrine bloqueia antes). Notas do G4 sem spec: `catch` estreito no `NotificationSender` e diferenças de rigor entre `ItemEditRules` e `ListingChangeRules`.

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0034`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (contrato inalterado para o gateway) | 2026-11-02 | (1) item não ativo responde cause `item.status.not_active` (antes `item.status.not_modifiable`), igual à fixture `error-item-not-active.json` e ao `MercadoLivreErrorMapper` da SPEC-0033; (2) leituras registradas: `value_id` de COLOR segue a fixture do ML (Preto `52049`, Branco `52055`); "anúncio de número par" = parte numérica do id; atributo de lista aplicado por `value_id` volta com o `value_name`; unidade de `number_unit` lida do `value_name`; o id do atributo aparece na `message` da cause; `/__sim` usa `value_id` em snake_case como o ML; 409 forçado no formato de erro do ML | Test-writer no G1: com `not_modifiable` o gateway traduziria item pausado como `ML_REJECTED` em vez de `ITEM_NOT_ACTIVE` | nenhuma | thomas (piloto automático do épico SPEC-0032) |

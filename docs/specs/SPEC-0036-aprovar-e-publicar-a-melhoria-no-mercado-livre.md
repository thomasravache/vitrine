---
id: SPEC-0036
title: Aprovar e publicar a melhoria no Mercado Livre
tier: full
type: feature
user_facing: false
status: approved
created: 2026-11-02
parent: SPEC-0032
depends_on: [SPEC-0035]
consumes_contract: [SPEC-0033@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/Listings/**, tests/Vitrine.UnitTests/Listings/**, tests/Vitrine.IntegrationTests/Listings/**, web/src/api/**]
adrs: [ADR-0001, ADR-0002, ADR-0010, ADR-0011, ADR-0014]
external: []
size: M
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0036 — Aprovar e publicar a melhoria no Mercado Livre

<!-- type: feature | fix | refactor | migration | foundation. user_facing: true quando a mudança altera uma jornada do usuário (UI ou API pública) — exige teste E2E. Substitua todos os marcadores com chaves duplas: o G0 (`spec_graph.py validate`) reprova a spec enquanto restar algum. As seções de Checklist em diante são preenchidas depois da aprovação, sem marcadores. -->

## 1. Visão Geral
Fecha o ciclo da melhoria no módulo `Listings`: o vendedor aprova os campos escolhidos, com os valores finais que ele editou, e o Vitrine publica no ML.

- `POST .../improvements/{id}/apply` revalida os valores finais, publica pelo editor da SPEC-0033 e devolve o **resultado por campo** (publicado, recusado com o motivo ou não enviado).
- Logo depois, relê o anúncio no ML e atualiza a cópia local (`ListingsUpsert`), para a tela mostrar o anúncio novo sem esperar a notificação.
- Registra quem aprovou, quando e o que foi enviado, e publica o evento `ListingImprovementApplied` v1.
- `POST .../{id}/discard` descarta a sugestão.
- Um segundo apply da mesma melhoria recebe 409: nada é publicado duas vezes.

## 2. Motivação & Escopo
**Motivação:** nada pode ser alterado no anúncio sem aprovação explícita (ADR-0002: é um dos erros mais caros do produto). A aprovação precisa publicar exatamente o que o vendedor viu e editou, contar o que deu certo e o que não deu, e não repetir a escrita se o vendedor clicar duas vezes.

**Objetivos (dentro do escopo):**
- `POST /api/v1/listings/{itemId}/improvements/{improvementId}/apply` (contrato no §6):
  - corpo com os valores finais por campo (título, descrição, atributos);
  - validação contra o retrato da categoria e do anúncio guardado na melhoria (`ListingChangeRules`) → 400 `VALIDATION_FAILED` sem enviar nada;
  - transição exclusiva (`apply_started_at` gravado com `UPDATE … WHERE status = 'ready' AND apply_started_at IS NULL`), para que dois applies simultâneos não publiquem duas vezes;
  - `IMercadoLivreListingEditor.ApplyAsync` (SPEC-0033), que relê o ML e aplica as regras de novo antes de escrever;
  - campo com `ML_UNAVAILABLE` (resultado incerto) confirmado pela releitura: se o valor no ML é o enviado, vira publicado;
  - status `applied` (tudo publicado), `partially_applied` (parte publicada) ou `ready` (nada publicado; o vendedor pode tentar de novo).
- Atualização imediata da cópia local com o anúncio relido (`IMercadoLivreItems.GetItemsAsync`, com a guarda de vendedor da SPEC-0029, + `ListingsUpsert`).
- Evento de integração `ListingImprovementApplied` v1 pelo outbox, quando ao menos um campo é publicado.
- Auditoria: tabela `listing_improvement_applications` (quem, quando, valores enviados, resultado por campo), apagada junto com a melhoria ao desconectar a conta.
- `POST .../{improvementId}/discard` → 204.
- O `result` do `GET .../latest` (contrato da SPEC-0035) passa a ser preenchido.
- Métrica `vitrine.listings.improvement_applications{outcome}`.

**Não-objetivos (fora do escopo):**
- Telas: SPEC-0038.
- Desfazer uma publicação (voltar ao texto anterior): épico futuro. O original fica guardado na melhoria para isso.
- Publicar em lote vários anúncios.
- Agendar a publicação.

## 3. Dependências
- **Implementações necessárias:** SPEC-0035, porque o apply opera sobre o agregado `ListingImprovement`, sua tabela e seus estados.
- **Contratos consumidos:** SPEC-0033@1:
  - `IMercadoLivreListingEditor.ApplyAsync`, `ListingChanges`, `FieldResult` e `MercadoLivreItemNotFoundException`;
  - `IMercadoLivreListingDetails.GetItemDetailsAsync` (confirmação de resultado incerto);
  - `ListingChangeRules` (validação do corpo).
  - Também usa `IMercadoLivreItems` v1 (SPEC-0021), já consumido pelo módulo.
  - Os testes usam um dublê do editor montado a partir do contrato; o CT-01 fecha o laço.
- **Pré-requisitos externos:** N/A.

## 4. Decisão Arquitetural
**Contexto:** segue o slice `Improvements` criado pela SPEC-0035 e os padrões do módulo `Listings`:
- endpoints em `Vitrine.Listings.Presentation/ListingsEndpoints.cs` (`HttpsRequiredFilter`, `AntiforgeryFilter`, `Problems`);
- atualização da cópia em `Infrastructure/Persistence/ListingsStores.cs` (`ListingsUpsert.ExecuteAsync`, com a regra `lastUpdated >=`);
- guarda de vendedor em `Infrastructure/Messaging/ItemChangedHandler.cs` (SPEC-0029);
- eventos pelo outbox como `WorkspaceCreated` (Identity) e os do MercadoLivre;
- projeto `Vitrine.Listings.IntegrationEvents` reservado pela SPEC-0022.

**Decisão:**
- Caso de uso `ApplyImprovementHandler` na `Application`, síncrono na requisição: o vendedor espera o resultado por campo (até 2 escritas no ML, ~10 s no pior caso com o 409).
- Exclusividade pelo `apply_started_at`. Um apply abandonado (processo caiu no meio) libera a melhoria depois de 5 min; antes disso, um novo apply recebe 409 `IMPROVEMENT_ALREADY_APPLIED`.
- Primeiro evento público do módulo em `Vitrine.Listings.IntegrationEvents`.
- A releitura do anúncio usa o mesmo caminho do `ItemChangedHandler` (guarda de vendedor + `ListingsUpsert`), extraído para um serviço compartilhado.

**Justificativa:**
- Síncrono porque o resultado por campo é a resposta que a tela mostra, e as escritas não são retentadas (SPEC-0033); um comando assíncrono só adiaria o mesmo resultado.
- A transição condicional no banco é a idempotência pedida pela ADR-0010, sem lock distribuído.
- Atualizar a cópia na hora evita que a tela mostre o título antigo até a notificação; a notificação que vier depois (eco do PUT) é inofensiva pela regra `lastUpdated >=`.

**Desvio do padrão existente:** nenhum.

**Alternativas descartadas:**
- Publicar por comando durável com polling: mais estados e mais espera, sem ganho (a escrita não é retentada).
- Confiar só na notificação para atualizar a cópia: atraso visível e dependência de um webhook sem garantia de entrega.
- Repetir o apply automaticamente em `ML_UNAVAILABLE`: risco de escrita dupla (ADR-0010); o vendedor decide tentar de novo.

**ADRs:** ADR-0001, ADR-0002, ADR-0010, ADR-0011, ADR-0014.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** apply com p95 < 5 s com o ML respondendo (2 leituras + até 2 escritas + 1 releitura), medido no IT com o dublê de latência do ML em 200 ms por chamada; 409 do ML acrescenta 2 s.
- **Segurança:**
  - sessão e workspace obrigatórios; melhoria de outro workspace ou de outro anúncio dá 404 — IT-03;
  - HTTPS e antiforgery nos dois POSTs — IT-03;
  - só os campos do corpo são publicados, e cada um passa por `ListingChangeRules` duas vezes (aqui e no editor) — UT-02, IT-03;
  - o corpo aceita só os campos do contrato (propriedade desconhecida dá 400).
- **Privacidade e dados pessoais:** a auditoria guarda o id do usuário que aprovou e os textos públicos do anúncio. Retenção até desconectar a conta (ADR-0006) — IT-08.
- **Disponibilidade e resiliência:**
  - ML indisponível antes de qualquer escrita: 502 `ML_UNAVAILABLE`, melhoria continua `ready`, tentativa registrada na auditoria — IT-04;
  - resultado incerto confirmado pela releitura — IT-05;
  - releitura da cópia falhou: a resposta sai com o resultado por campo e `listing: null`, e a notificação ou a reconciliação corrigem a cópia (SPEC-0022) — IT-05;
  - idempotência: segundo apply dá 409, e o editor é chamado uma vez — IT-03.
- **Acessibilidade (UI):** N/A — sem tela (SPEC-0038).
- **Custo:** N/A — API do ML sem custo; nenhuma chamada de IA.

## 6. Artefato A — Contrato
**Interface:**
- `POST /api/v1/listings/{itemId}/improvements/{improvementId}/apply`
- `POST /api/v1/listings/{itemId}/improvements/{improvementId}/discard`
- evento `ListingImprovementApplied` v1

```text
Exigem sessão, HTTPS e antiforgery; qualquer membro do workspace. Erros problem+json com "code".

POST /api/v1/listings/{itemId}/improvements/{improvementId}/apply
  body { fields: { title?: string, description?: string, attributes?: [{ id, valueId?: string, valueName?: string }] } }
       valores FINAIS (depois da edição do vendedor); campo ausente = não publicar; ao menos um campo
  200 { improvementId, status: "applied"|"partially_applied"|"ready",
        fields: { title?: FieldOutcome, description?: FieldOutcome, attributes?: [{ id, ...FieldOutcome }] },
        listing: ListingResponse|null }     // cópia local atualizada (formato do GET /listings da SPEC-0022)
        FieldOutcome = { result: "applied"|"rejected"|"not_attempted", code: string|null, message: string|null }
        status ready = nada publicado (todos rejeitados ou não enviados); a melhoria aceita novo apply
  400 VALIDATION_FAILED { errors: { "title"|"description"|"attributes.{id}"|"fields": [código] } }
        corpo vazio, campo desconhecido, atributo fora da melhoria, ou violação de ListingChangeRules contra o retrato
        guardado; nada é enviado ao ML
  404 NOT_FOUND                         melhoria ou anúncio fora do workspace, ou melhoria de outro anúncio
  409 IMPROVEMENT_NOT_READY             generating | failed | discarded
  409 IMPROVEMENT_ALREADY_APPLIED       applied | partially_applied, ou apply em andamento (< 5 min)
  409 ACCOUNT_REAUTHORIZATION_REQUIRED  conta em reautorização (antes ou durante)
  502 ML_UNAVAILABLE                    releitura inicial do editor falhou: nada foi publicado; melhoria segue ready

  Passos: valida -> marca apply_started_at (condicional) -> ApplyAsync -> confirma incertos com GetItemDetailsAsync
          -> GetItemsAsync([itemId]) + guarda de vendedor + ListingsUpsert -> grava result, status, auditoria e
          evento (uma transação) -> limpa apply_started_at
  MercadoLivreItemNotFoundException do editor -> 404 NOT_FOUND e apaga a cópia local do anúncio

POST /api/v1/listings/{itemId}/improvements/{improvementId}/discard
  204                                   ready | failed -> discarded; já discarded -> 204 (idempotente)
  404 NOT_FOUND
  409 IMPROVEMENT_NOT_READY             generating
  409 IMPROVEMENT_ALREADY_APPLIED       applied | partially_applied

Evento (Vitrine.Listings.IntegrationEvents), v1, publicado quando ao menos um campo é publicado:
  ListingImprovementApplied { improvementId: uuid, workspaceId: uuid, accountId: uuid, itemId: string,
    appliedFields: [string], rejectedFields: [string], notAttemptedFields: [string],   // "title" | "description" | "attributes.{id}"
    approvedBy: uuid, occurredAt: datetime(UTC) }

Persistência (schema listings):
  listing_improvement_applications(id uuid pk, improvement_id fk -> listing_improvements on delete cascade,
    approved_by uuid, approved_at timestamptz, sent jsonb, results jsonb,
    outcome: applied | partially_applied | nothing_applied | ml_unavailable | not_found)
  listing_improvements: status, result (jsonb no formato do "result" do latest), apply_started_at

Métrica: vitrine.listings.improvement_applications{outcome}
```

**Design:** N/A — sem tela. As telas da SPEC-0038 consomem este contrato.

**Arquivos/módulos afetados:** ver `touches`. `Application/Improvements/ApplyImprovementHandler.cs` e `DiscardImprovementHandler.cs`, `Infrastructure` (persistência, migração, serviço de releitura extraído do `ItemChangedHandler`), `Presentation` (endpoints), `IntegrationEvents/ListingImprovementApplied.cs`, cliente orval regenerado.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Publicar tudo | melhoria `ready`, valores válidos | 200 `applied`; cópia local com o anúncio novo; evento; auditoria com os valores editados | UT-01, IT-01, CT-02 |
| Publicação parcial | o ML recusa um campo | 200 `partially_applied`, com código e mensagem do campo recusado | UT-01, IT-02 |
| Nada publicado | todos recusados ou não enviados | 200 `ready`; sem evento; novo apply permitido | UT-01, IT-02 |
| Corpo inválido | vazio, campo ou atributo desconhecido, regra violada | 400 `VALIDATION_FAILED`; nada enviado | UT-02, IT-03 |
| Melhoria alheia | outro workspace, outro anúncio, inexistente | 404 | IT-03 |
| Não pronta | `generating`, `failed`, `discarded` | 409 `IMPROVEMENT_NOT_READY` | IT-03 |
| Segundo apply | já publicada, ou dois simultâneos | 409 `IMPROVEMENT_ALREADY_APPLIED`; editor chamado uma vez | IT-03 |
| Conta em reautorização | conta `paused` ou exceção do editor | 409 `ACCOUNT_REAUTHORIZATION_REQUIRED` | IT-03 |
| Sem antiforgery | POST sem token | 400 | IT-03 |
| ML indisponível | editor lança `Unavailable` | 502 `ML_UNAVAILABLE`; melhoria `ready`; tentativa auditada | IT-04 |
| Resultado incerto | campo `ML_UNAVAILABLE` e o ML relido mostra o valor enviado | campo vira publicado | UT-01, IT-05 |
| Releitura falhou | `GetItemsAsync` indisponível | 200 com resultados e `listing: null`; log Warning | IT-05 |
| Anúncio sumiu | editor lança `ItemNotFound` | 404; cópia local apagada | IT-05 |
| Apply abandonado | `apply_started_at` com mais de 5 min | novo apply permitido | IT-06 |
| Descartar | `ready`/`failed`; repetido; publicada; gerando | 204; 204; 409; 409 | IT-07 |
| Eco da notificação | `MercadoLivreItemChanged` depois do apply | cópia não regride; guarda de vendedor mantida | IT-01 |
| Desconectar | conta desconectada | melhorias e auditoria apagadas | IT-08 |
| Latest com resultado | após o apply | `result` com quem aprovou e o resultado por campo | IT-01, CT-02 |

## 7. Artefato B — Plano de Testes (TDD)
<!-- Todo teste tem um ID e é escrito ANTES da implementação (Red). No código, cada teste carrega a tag `SPEC-0036:<ID>` — o G1 (`spec_graph.py verify`) verifica. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem testes: "N/A — motivo" SEM ID. Teste que protege comportamento existente e passa antes da mudança: marque `(guarda: passa antes da mudança)`. Estratégia completa: references/testing.md da skill. -->

### 7.1 Testes de Caracterização
- **CH-01** — Dado o `ItemChangedHandler` atual, quando os testes da SPEC-0022 (IT 09) e da SPEC-0029 (IT 01) rodam depois da extração do serviço de releitura, então continuam passando: anúncio mais novo atualiza, mais antigo é ignorado, 404 apaga, anúncio de outro vendedor nunca é gravado (guarda: passa antes da mudança).

### 7.2 Testes Unitários
- **UT-01** — Dado resultados por campo do editor, quando o desfecho é calculado, então:
  - todos `Applied` dá `applied`; ao menos um `Applied` e um não dá `partially_applied`; nenhum `Applied` dá `ready`;
  - um campo `Rejected(ML_UNAVAILABLE)` cujo valor relido é igual ao enviado (texto comparado sem diferença de espaços finais; atributo por `valueId` ou `valueName` normalizado) vira `Applied`; diferente, continua recusado;
  - o evento lista os campos em `appliedFields`, `rejectedFields` e `notAttemptedFields`.
- **UT-02** — Dado o retrato da melhoria (categoria e anúncio guardados) e corpos de apply, quando validados, então corpo sem campos, propriedade desconhecida, atributo que não está na melhoria, título acima do limite, descrição com HTML e valor fora da lista dão `VALIDATION_FAILED` com o mapa de erros por campo; um corpo válido gera exatamente o `ListingChanges` correspondente, com atributos normalizados.

### 7.3 Testes de Integração
<!-- Postgres real + Wolverine real; IMercadoLivreListingEditor, IMercadoLivreListingDetails e IMercadoLivreItems por dublês do contrato SPEC-0033@1/SPEC-0021@1. Melhoria criada pelo fluxo da SPEC-0035 com o provedor simulado. -->
- **IT-01** — Dado uma melhoria `ready` e o dublê do editor devolvendo `Applied` para tudo, quando o usuário faz o apply com o título editado (diferente da sugestão), então:
  - responde 200 `applied`, com `listing` já com o título novo (p95 < 5 s com 200 ms por chamada no dublê);
  - o editor recebe exatamente os valores do corpo;
  - `GET /listings` mostra o título novo, e o `GET .../latest` traz `result` com `approvedBy`, `approvedAt` e o resultado por campo;
  - a auditoria guarda o usuário, o horário e os valores enviados;
  - `ListingImprovementApplied` vai para o outbox;
  - um `MercadoLivreItemChanged` do mesmo anúncio processado em seguida, com `lastUpdated` igual ou anterior, não regride a cópia.
- **IT-02** — Dado o editor devolvendo título `Rejected(FIELD_NOT_UPDATABLE)` e descrição `Applied`, quando o apply roda, então responde 200 `partially_applied` com o código e a mensagem do título, e o evento lista o título em `rejectedFields`. Com tudo recusado ou não enviado, responde 200 `ready`, sem evento, e um novo apply é aceito.
- **IT-03** — Dado dois workspaces, então:
  - melhoria de outro workspace, de outro anúncio ou inexistente dá 404;
  - melhoria `generating`, `failed` ou `discarded` dá 409 `IMPROVEMENT_NOT_READY`;
  - um 2º apply depois de `applied` dá 409 `IMPROVEMENT_ALREADY_APPLIED`; 2 applies simultâneos dão um 200 e um 409, e o editor é chamado 1 vez;
  - conta em reautorização (no banco ou pela exceção do editor) dá 409 `ACCOUNT_REAUTHORIZATION_REQUIRED`;
  - corpo inválido dá 400 `VALIDATION_FAILED`, e o editor não é chamado;
  - POST sem antiforgery dá 400.
- **IT-04** — Dado o editor lançando `MercadoLivreUnavailableException`, quando o apply roda, então responde 502 `ML_UNAVAILABLE`, a melhoria continua `ready` sem `apply_started_at`, a auditoria registra a tentativa com `outcome=ml_unavailable`, e um novo apply depois funciona.
- **IT-05** — Dado resultados incertos e falhas na releitura, então:
  - descrição `Rejected(ML_UNAVAILABLE)` e o dublê de detalhe com o texto enviado: o campo vira `applied`;
  - `GetItemsAsync` indisponível: 200 com o resultado por campo, `listing: null` e log Warning;
  - editor lançando `MercadoLivreItemNotFoundException`: 404, e a cópia local do anúncio é apagada.
- **IT-06** — Dado uma melhoria `ready` com `apply_started_at` gravado há 6 min (relógio falso), quando um novo apply chega, então ele é aceito. Com 1 min, responde 409 `IMPROVEMENT_ALREADY_APPLIED`.
- **IT-07** — Dado melhorias em cada estado, quando o usuário descarta, então `ready` e `failed` dão 204 e ficam `discarded`; repetir dá 204; `applied`/`partially_applied` dão 409 `IMPROVEMENT_ALREADY_APPLIED`; `generating` dá 409 `IMPROVEMENT_NOT_READY`; de outro workspace dá 404; sem antiforgery dá 400. Descartar não devolve a cota.
- **IT-08** — Dado uma melhoria publicada com auditoria, quando `MercadoLivreAccountDisconnected` é processado, então não restam linhas de melhoria nem de auditoria da conta.

### 7.4 Testes de Contrato
- **CT-01** — Dado os tipos públicos do contrato SPEC-0033@1 (`ListingChanges`, `FieldResult`, exceções) e as fixtures de `tests/Fixtures/MercadoLivre/`, quando o dublê do editor e o de detalhe são montados a partir deles e o apply roda, então cada `FieldResult` é traduzido para o `FieldOutcome` do §6. É o lado consumidor do contrato. Inclui: o payload de `ListingImprovementApplied` publicado valida contra o schema v1 do §6 (lado provedor do evento).
- **CT-02** — Dado o documento OpenAPI gerado, quando comparado com o §6, então `apply` e `discard` têm corpo, campos, tipos e códigos de resposta documentados, o `result` do `latest` (SPEC-0035) está preenchido no formato combinado, e o cliente orval regenerado compila. A SPEC-0038 consome este contrato.

### 7.5 Testes E2E
- N/A — sem tela nesta spec. A publicação é provada na jornada `aprovar-melhoria` da SPEC-0038 (E2E 01, 03 e 05), contra o simulador do ML.

### 7.6 Outros
- Desempenho: IT-01 (p95 do apply com latência simulada).

**Dublês e dados de teste:**
- dublês de `IMercadoLivreListingEditor` (resultados configuráveis por campo, contagem de chamadas, atraso), `IMercadoLivreListingDetails` e `IMercadoLivreItems`, fiéis aos contratos SPEC-0033@1 e SPEC-0021@1;
- melhorias criadas pelo fluxo da SPEC-0035 com o provedor simulado;
- `TimeProvider` falso;
- builders de anúncio e conta.

**Ambiente de execução:** UT em processo; IT com Testcontainers (local e CI).

## 8. Plano de Rollout
- **Estratégia:** deploy direto. Sem a tela (SPEC-0038), só a API existe; com o simulador, toda escrita vai para o ML simulado.
- **Dados/schema:** migração só de criação (tabela `listing_improvement_applications`, chave estrangeira com `on delete cascade`). Reversível.
- **Compatibilidade:** endpoints novos; o `result` do `latest` (SPEC-0035) já existia como `null` e passa a ser preenchido; primeiro evento público do `Listings`, v1.
- **Observabilidade:**
  - `vitrine.listings.improvement_applications{outcome}`;
  - log Information por apply (melhoria, campos, desfecho, duração, sem o texto) e Warning em `ml_unavailable`, `nothing_applied` e falha da releitura.
- **Rollback:** revert do merge. Publicações feitas no ML não são desfeitas pelo revert; o original fica guardado na melhoria.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Publicar de forma síncrona ou por comando? — Síncrona: o vendedor espera o resultado por campo, e as escritas não são retentadas (SPEC-0033). (Architect, 2026-11-02, a confirmar no H1)
- [x] Quem pode aprovar? — Qualquer membro do workspace, a mesma regra de pedir a melhoria (SPEC-0035). Revisto quando existirem papéis no épico de contas. (Architect, 2026-11-02, a confirmar no H1)
- [x] Nada publicado conta como aplicado? — Não. A melhoria volta a `ready` e aceita novo apply, para o vendedor ajustar e tentar de novo sem gastar outra geração. (Architect, 2026-11-02, a confirmar no H1)
- [x] Descartar devolve a cota? — Não: a geração já foi feita e paga. (Architect, 2026-11-02, a confirmar no H1)
- [x] Quanto tempo vale um apply em andamento? — 5 min; depois disso a melhoria é liberada para novo apply (processo que caiu no meio). (Architect, 2026-11-02, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [ ] CH-01 escrito, passando no código atual (sincronização local do anúncio), commitado antes de qualquer mudança

**Fase 1: Regras de publicação**
- [ ] Red: escrever UT-01..UT-02 com a tag `SPEC-0036:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: revalidação, seleção de campos e trava `apply_started_at`
- [ ] Refactor mantendo tudo verde

**Fase 2: Aplicar e descartar**
- [ ] Red: escrever IT-01..IT-08 e CT-01..CT-02 com a tag `SPEC-0036:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: endpoints `apply`/`discard`, resultado por campo, atualização do anúncio local e evento `ListingImprovementApplied` v1
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0036`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

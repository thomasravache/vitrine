---
id: SPEC-0022
title: Importação e sincronização de anúncios (módulo Listings)
tier: full
type: feature
user_facing: false
status: approved
created: 2026-10-30
parent: SPEC-0019
depends_on: []
consumes_contract: [SPEC-0020@1, SPEC-0021@1, SPEC-0023@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/Listings/**, src/Api/**, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.ArchitectureTests/**, web/src/api/**, Vitrine.slnx, tests/Vitrine.Tooling.Tests/packages.lock.json, web/e2e/support/packages.lock.json, src/AppHost/packages.lock.json]
adrs: [ADR-0001, ADR-0010, ADR-0011, ADR-0014]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-30
---
# SPEC-0022 — Importação e sincronização de anúncios (módulo Listings)

## 1. Visão Geral
Cria o módulo `Listings`, com a cópia local dos anúncios das contas conectadas: título, preço, estoque, vendidos, estado, link e miniatura.

A cópia é mantida em quatro momentos:
- **ao conectar:** importação completa;
- **sob demanda:** botão "Sincronizar agora";
- **todo dia:** reconciliação, que recupera notificações perdidas (ADR-0010);
- **a cada notificação** de anúncio alterado (SPEC-0023).

Expõe a listagem paginada com busca e filtros e um resumo para as telas.

## 2. Motivação & Escopo
**Motivação:** os próximos épicos (melhorar anúncios, responder perguntas) trabalham sobre os anúncios do vendedor, que agora precisam estar no Vitrine. Consultar o ML a cada tela seria lento e esbarraria em limites. A cópia local, mantida por eventos e reconciliação, é o padrão da ADR-0010/0014.

**Objetivos (dentro do escopo):**
- Módulo `src/Modules/Listings` (padrão ADR-0001), com schema `listings`, migração inicial e projeto `IntegrationEvents` vazio, reservado.
- Handlers Wolverine duráveis:
  - `MercadoLivreAccountConnected` → importação completa;
  - `MercadoLivreAccountDisconnected` → apaga anúncios e estado da conta;
  - `MercadoLivreAccountReauthorizationRequired` → sincronização da conta em `paused` (anúncios mantidos);
  - `MercadoLivreItemChanged` → relê e atualiza o anúncio.
- Importação: para cada status (`active`, `paused`, `closed`), busca os IDs (`IMercadoLivreItems.SearchItemIdsAsync`) e lê em lotes (`GetItemsAsync`). Faz upsert e, ao terminar com sucesso, apaga os anúncios da conta que não apareceram (marcar e varrer).
- Uma sincronização por conta por vez.
- Reconciliação a cada `Listings:ReconciliationInterval` (padrão 24 h) de todas as contas `connected`. Isso também mantém os tokens em uso: o ML invalida tokens após 4 meses sem chamada.
- Endpoints: `GET /api/v1/listings`, `GET /api/v1/listings/summary` e `POST /api/v1/listings/sync` (contrato abaixo).
- Métricas:
  - `vitrine.listings.sync.duration` (histograma, por `trigger`);
  - `vitrine.listings.sync.runs{trigger,result}`;
  - `vitrine.listings.items_upserted`.

**Não-objetivos (fora do escopo):**
- Detalhe do anúncio (descrição, atributos, fotos), além da miniatura: épico "Melhorar anúncios".
- Escrever no ML.
- Busca textual avançada (relevância, acentos). A busca é por trecho do título, sem diferenciar maiúsculas, ou pelo código exato `MLB…`.
- Retomar uma importação interrompida do ponto em que parou. Falhou: a mensagem é retentada pelo Wolverine e recomeça do início, de forma idempotente. O `scroll_id` do ML expira em 5 min, o que torna inviável retomar pelo cursor.

## 3. Dependências
- **Implementações necessárias:** N/A.
- **Contratos consumidos:**
  - SPEC-0020@1: eventos `MercadoLivreAccountConnected`/`Disconnected` e `IWorkspaceMembership`.
  - SPEC-0021@1: `IMercadoLivreItems` e o evento `MercadoLivreAccountReauthorizationRequired`.
  - SPEC-0023@1: evento `MercadoLivreItemChanged`.
  - Os testes usam dublês construídos a partir desses contratos, e os CT fecham o laço.
- **Pré-requisitos externos:** N/A.

## 4. Decisão Arquitetural
**Contexto:** a referência é o módulo `src/Modules/Identity`:
- registro do módulo, DbContext com outbox, migrações;
- endpoints com `MapGroup` e `Problems`;
- handlers Wolverine com fila local durável, como `LocalQueueFor<WorkspaceCreated>().UseDurableInbox()`;
- a durabilidade é provada em `tests/Vitrine.IntegrationTests/WolverineDurabilityTests.cs`.

O workspace e o papel do usuário vêm de `Identity.PublicApi` (SPEC-0020).

**Decisão:**
- O módulo `Listings` consome apenas `MercadoLivre.PublicApi`/`IntegrationEvents` e `Identity.PublicApi`.
- A importação é um comando durável `SyncListings(accountId, trigger)`, com a exclusividade por conta garantida pela linha `sync_states` (status `running` com `UPDATE … WHERE status <> 'running'`).
- A reconciliação é agendada por um `BackgroundService` leve que publica `ReconcileListings` no intervalo configurado. O trabalho em si fica no handler durável.
- A paginação da listagem usa cursor (keyset por `ml_last_updated desc, item_id`).

**Justificativa:**
- Eventos e outbox seguem a ADR-0014. A reconciliação cumpre a ADR-0010.
- O keyset é estável enquanto a sincronização altera a tabela, o que o offset não garante.

**Desvio do padrão existente:** nenhum.

**Alternativas descartadas:**
- Consultar o ML em cada abertura da tela: lento, sujeito a limites, sem busca local.
- Agendamento por cron externo: o Wolverine já dá durabilidade, sem serviço novo.
- Paginação por offset: duplica ou pula itens durante a sincronização.

**ADRs:** ADR-0001, ADR-0010, ADR-0011, ADR-0014.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:**
  - `GET /listings` com p95 < 150 ms para workspaces com até 10.000 anúncios, com índices em `(workspace_id, status, ml_last_updated desc, item_id)` e em `lower(title)` (trigram, se disponível na imagem do Postgres; senão btree). Medido no IT-11 com 10.000 linhas sintéticas: a consulta tem < 150 ms no container.
  - Importação de 1.000 anúncios em < 2 min, dominada pela API do ML (50 chamadas bulk), medida pelo `vitrine.listings.sync.duration`.
- **Segurança:**
  - todo endpoint exige sessão e filtra pelo workspace do usuário; `accountId` de outro workspace responde 404 no sync e lista vazia no filtro — IT-07;
  - antiforgery no POST — IT-08;
  - parâmetros validados (`limit` 1..50, `status` da lista, `q` até 100 caracteres) — IT-03.
- **Privacidade e dados pessoais:** os anúncios são dados públicos do vendedor no ML. Não há dado pessoal de comprador. Retenção até desconectar a conta — IT-06.
- **Disponibilidade e resiliência:**
  - `Unavailable` do gateway: sincronização `failed` com `lastError`, e a mensagem é retentada pelo Wolverine (3 tentativas com backoff de 1, 5 e 15 min);
  - `ReauthorizationRequired`: `paused`, sem retentar;
  - anúncios mantidos em toda falha;
  - upsert idempotente;
  - notificação fora de ordem (com `last_updated` mais antigo que o gravado) é ignorada — IT-09, IT-10.
- **Acessibilidade (UI):** N/A — sem tela (SPEC-0025).
- **Custo:** N/A — sem serviço pago. O armazenamento de cerca de 1 KB por anúncio é desprezível no Postgres atual.

## 6. Artefato A — Contrato
**Interface:**
- `GET /api/v1/listings`
- `GET /api/v1/listings/summary`
- `POST /api/v1/listings/sync`
- handlers dos eventos consumidos

```text
Todos exigem sessão; qualquer membro do workspace. Erros problem+json com "code". Usuário sem workspace -> 403 FORBIDDEN.

GET /api/v1/listings?status=active|paused|closed|all (padrão active)&accountId={uuid?}&q={texto?}&limit={1..50, padrão 20}&cursor={opaco?}
  200 -> { items: [{ itemId: "MLB…", accountId, title, price: decimal, currencyId, availableQuantity, soldQuantity,
                     status, permalink: url|null, thumbnailUrl: url|null, lastUpdated: datetime }],
           nextCursor: string|null }
         ordenado por lastUpdated desc, itemId; "all" inclui qualquer status; q: trecho do título (case-insensitive)
         ou código exato (q casa ^MLB\d+$ -> filtra por itemId)
  400 VALIDATION_FAILED { errors }   limit/status/q/cursor inválidos

GET /api/v1/listings/summary
  200 -> { total, byStatus: { active, paused, closed, other },
           accounts: [{ accountId, itemCount, syncStatus: "idle"|"running"|"failed"|"paused", lastSyncedAt: datetime|null,
                        lastError: "ML_UNAVAILABLE"|null }] }

POST /api/v1/listings/sync    (antiforgery)
  body: { accountId?: uuid }   ausente = todas as contas connected do workspace
  202 -> { queued: [accountId] }      enfileira SyncListings(accountId, "manual"); conta já em running não é reenfileirada
                                      (aparece em queued mesmo assim: a sincronização em curso atende o pedido)
  404 NOT_FOUND                       accountId inexistente ou de outro workspace
  409 ACCOUNT_REAUTHORIZATION_REQUIRED  a conta pedida está em paused por reautorização
  429 SYNC_TOO_SOON { retryAfterSeconds }  última sincronização manual da conta concluída há menos de 5 min

Handlers (filas locais duráveis):
  MercadoLivreAccountConnected      -> cria/zera sync_states(accountId, workspaceId) e enfileira SyncListings(accountId, "connected")
  MercadoLivreAccountDisconnected   -> apaga listings e sync_states da conta
  MercadoLivreAccountReauthorizationRequired -> sync_states.status = paused
  MercadoLivreItemChanged           -> GetItemsAsync([itemId]): Found -> upsert (se lastUpdated >= gravado);
                                       NotFound -> apaga; conta desconhecida -> ignora
  SyncListings(accountId, trigger)  -> marca running (se já running: descarta); para status in [active, paused, closed]:
                                       SearchItemIdsAsync -> GetItemsAsync em lotes -> upsert com sync_run_id;
                                       sucesso: apaga da conta os anúncios com sync_run_id antigo, status idle, item_count,
                                       completed_at; ReauthorizationRequired -> paused; Unavailable -> failed + retry
  ReconcileListings                 -> enfileira SyncListings(accountId, "reconciliation") para cada conta conhecida não paused

Persistência (schema "listings"):
  listings(account_id uuid, item_id varchar(32), workspace_id uuid, title, price numeric(14,2), currency_id, available_quantity,
           sold_quantity, status, permalink, thumbnail_url, ml_last_updated timestamptz, sync_run_id uuid, updated_at,
           PK(account_id, item_id))
  sync_states(account_id uuid pk, workspace_id, status, started_at, completed_at, last_manual_at, item_count, last_error)

Configuração: Listings:ReconciliationInterval (padrão 1.00:00:00), Listings:ManualSyncCooldown (padrão 00:05:00).
```

**Design:** N/A — sem tela nesta spec. A tela Anúncios (SPEC-0025) consome este contrato.

**Arquivos/módulos afetados:** ver `touches`.
- `src/Modules/Listings/Vitrine.Listings.{Domain,Application,Infrastructure,Presentation,IntegrationEvents}`.
- Registro em `src/Api`.
- Cliente regenerado em `web/src/api`.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Importar ao conectar | `MercadoLivreAccountConnected` | anúncios dos 3 status gravados, `sync_states` idle com `item_count` | UT-01, IT-01, CT-01 |
| Reimportar | anúncio sumiu do ML ou mudou | removido ou atualizado; nada duplica | UT-01, IT-02 |
| Listar | filtros, busca, cursor | página ordenada e estável, só do workspace | UT-02, IT-03, CT-02 |
| Parâmetro inválido | `limit=0`, status desconhecido, cursor adulterado | 400 VALIDATION_FAILED | UT-02, IT-03 |
| Resumo | anúncios e contas | totais por status e estado por conta | IT-04, CT-02 |
| Reconciliação | intervalo passou | toda conta não `paused` ressincronizada; alteração perdida corrigida | IT-05 |
| Desconectar | `MercadoLivreAccountDisconnected` | anúncios e estado da conta apagados | IT-06, CT-01 |
| Isolamento | workspace B | não vê nem sincroniza anúncios de A (lista vazia / 404) | IT-07 |
| Sincronizar agora | POST sync | 202; em `running` não duplica; < 5 min responde 429; conta alheia 404; em reautorização 409 | IT-08 |
| Notificação de item | `MercadoLivreItemChanged` | relê; atualiza, apaga (404) ou ignora se mais antigo | UT-03, IT-09, CT-01 |
| Falhas do ML | `Unavailable` / `ReauthorizationRequired` | `failed` + retry / `paused` sem retry; anúncios mantidos | IT-10, CT-01 |
| Desempenho da listagem | 10.000 anúncios | consulta < 150 ms | IT-11 |
| Sem antiforgery | POST sync sem token | 400 | IT-08 |
| Fronteiras | módulo Listings | só contratos públicos de outros módulos | UT-04 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — módulo novo.

### 7.2 Testes Unitários
- **UT-01** — Dado o algoritmo de sincronização com um dublê de `IMercadoLivreItems`, quando roda duas vezes (a 2ª sem um ID e com outro alterado), então:
  - o resultado final tem só os IDs vistos na 2ª execução, com os dados novos;
  - IDs repetidos entre páginas não duplicam;
  - a varredura só acontece se todos os status terminarem.
- **UT-02** — Dado os parâmetros da listagem, quando validados e convertidos em consulta, então:
  - `status`, `limit` (1..50) e `q` (≤ 100) são validados;
  - `q` no formato `MLB\d+` vira filtro por código;
  - o cursor codifica e decodifica `(lastUpdated, itemId)`;
  - cursor adulterado é recusado.
- **UT-03** — Dado um anúncio gravado com `ml_last_updated = T`, quando chega um `Found` com `LastUpdated < T`, então é ignorado; com `≥ T`, é aplicado.
- **UT-04** — Dado os assemblies do módulo Listings, quando as regras de arquitetura rodam, então:
  - passam as regras de camadas e isolamento;
  - Listings só referencia `MercadoLivre.PublicApi`, `MercadoLivre.IntegrationEvents` e `Identity.PublicApi`;
  - o schema é próprio.

### 7.3 Testes de Integração
<!-- Postgres real + Wolverine real (tracking de mensagens). IMercadoLivreItems substituído por dublê do contrato SPEC-0021@1. -->
- **IT-01** — Dado o dublê com 3 anúncios ativos, 1 pausado e 1 encerrado, quando `MercadoLivreAccountConnected` é publicado, então os 5 anúncios ficam gravados com o workspace do evento, e `sync_states` fica `idle` com `item_count=5`.
- **IT-02** — Dado uma conta importada, quando o dublê passa a omitir 1 anúncio e a alterar o preço de outro e a sincronização roda de novo, então o anúncio omitido some, o preço é atualizado e o total de linhas confere (sem duplicatas).
- **IT-03** — Dado 45 anúncios em 2 contas, quando `GET /listings` é chamado:
  - com filtros de status, `accountId` e `q` por trecho e por `MLB…`, e paginando com `limit=20` pelo `nextCursor` até o fim, cada item aparece exatamente uma vez, na ordem do contrato;
  - com `limit=0`, `status=xyz` ou cursor adulterado, responde 400 VALIDATION_FAILED.
- **IT-04** — Dado contas em `idle`, `failed` e `paused`, quando `GET /listings/summary` é chamado, então os totais por status e o estado por conta conferem.
- **IT-05** — Dado uma conta importada e um anúncio alterado só no dublê (notificação perdida), quando `ReconcileListings` é processado, então a conta é ressincronizada e o anúncio fica atualizado. Contas `paused` não são enfileiradas.
- **IT-06** — Dado uma conta importada, quando `MercadoLivreAccountDisconnected` é processado, então não restam linhas em `listings` nem em `sync_states` para ela.
- **IT-07** — Dado workspaces A e B com anúncios, quando o usuário de B lista, filtra por `accountId` de A ou pede sync da conta de A, então recebe só os próprios anúncios, lista vazia no filtro e 404 no sync.
- **IT-08** — Dado uma conta, quando `POST /listings/sync`:
  - responde 202, e a sincronização roda;
  - um 2º pedido durante `running` não enfileira outra;
  - um pedido < 5 min após uma manual concluída responde 429 com `retryAfterSeconds`;
  - conta em reautorização responde 409;
  - sem antiforgery, responde 400.
- **IT-09** — Dado um anúncio gravado, quando chega `MercadoLivreItemChanged` e o dublê devolve `Found` mais novo, `NotFound` ou `Found` mais antigo, então o anúncio é atualizado, apagado ou mantido, respectivamente. Para conta desconhecida, nada acontece.
- **IT-10** — Dado o dublê lançando `MercadoLivreUnavailableException` no meio da importação, então `sync_states` fica `failed` com `lastError=ML_UNAVAILABLE`, os anúncios anteriores ficam intactos e a mensagem é reagendada. Lançando `ReauthorizationRequired`, fica `paused`, sem reagendamento.
- **IT-11** — Dado 10.000 anúncios sintéticos num workspace, quando `GET /listings?status=active&q=<trecho>` é consultado 20 vezes, então o p95 do tempo da consulta é < 150 ms.

### 7.4 Testes de Contrato
- **CT-01** — Dado os payloads JSON de exemplo de `MercadoLivreAccountConnected`, `MercadoLivreAccountDisconnected` (SPEC-0020@1), `MercadoLivreAccountReauthorizationRequired` (SPEC-0021@1) e `MercadoLivreItemChanged` (SPEC-0023@1), quando desserializados pelos tipos publicados e entregues aos handlers do Listings, então são aceitos e processados. É o lado consumidor dos contratos.
- **CT-02** — Dado o documento OpenAPI gerado, quando comparado com o contrato da seção 6, então `GET /listings`, `GET /listings/summary` e `POST /listings/sync` têm os campos, tipos e códigos de resposta documentados. A SPEC-0025 consome esse contrato.

### 7.5 Testes E2E
- N/A nesta spec: a jornada "ver anúncios" é a SPEC-0025:E2E-01, contra o simulador.

### 7.6 Outros
- Desempenho: IT-11.

**Dublês e dados de teste:**
- dublê em memória de `IMercadoLivreItems`, fiel ao contrato SPEC-0021@1 (páginas, `Found`/`NotFound`, exceções);
- builders de anúncio;
- eventos publicados pelo Wolverine de teste.

**Ambiente de execução:** IT com Testcontainers (local e CI).

## 8. Plano de Rollout
- **Estratégia:** deploy direto. Sem contas conectadas, nada roda além do agendador (sem trabalho).
- **Dados/schema:** migração inicial do schema `listings`, só criação, pelo `--migrate`.
- **Compatibilidade:** endpoints novos, com o cliente OpenAPI regenerado.
- **Observabilidade:**
  - `vitrine.listings.sync.duration{trigger}`;
  - `vitrine.listings.sync.runs{trigger,result=succeeded|failed|paused}`;
  - `vitrine.listings.items_upserted`;
  - log Information por sincronização concluída (conta, total, duração) e Warning em `failed`.
- **Rollback:** revert do merge. As tabelas ficam sem uso.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Importar anúncios encerrados? — Sim (`closed` entra no filtro "Encerrados" da tela). O custo é de chamadas na importação; o volume é acompanhado pela métrica. (Architect, 2026-10-30, a confirmar no H1)
- [x] Intervalo da reconciliação? — 24 h (configurável). Cobre a janela de 2 dias do `missed_feeds` e mantém os tokens ativos. (Architect, 2026-10-30, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Regras de sincronização**
- [ ] Red: escrever UT-01..UT-04 com a tag `SPEC-0022:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: marcar e varrer, cursor keyset, intervalo mínimo entre syncs e mapeamento de status
- [ ] Refactor mantendo tudo verde

**Fase 2: Módulo Listings**
- [ ] Red: escrever IT-01..IT-11, CT-01 e CT-02 com a tag `SPEC-0022:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: handlers de `Connected`/`Disconnected`/`ItemChanged`, `SyncListings`, `ReconcileListings` (24 h), `GET /listings`, `/summary`, `POST /sync` e cliente OpenAPI regenerado
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0022`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

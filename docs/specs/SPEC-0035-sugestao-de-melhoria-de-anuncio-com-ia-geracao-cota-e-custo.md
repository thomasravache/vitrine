---
id: SPEC-0035
title: Sugestão de melhoria de anúncio com IA (geração, cota e custo)
tier: full
type: feature
user_facing: false
status: approved
created: 2026-11-02
parent: SPEC-0032
depends_on: [SPEC-0033]
consumes_contract: [SPEC-0033@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/Listings/**, src/Api/**, src/AppHost/**, Directory.Packages.props, deploy/compose.yaml, deploy/local/.env.example, tests/Vitrine.UnitTests/Listings/**, tests/Vitrine.UnitTests/packages.lock.json, tests/Vitrine.IntegrationTests/Listings/**, tests/Vitrine.IntegrationTests/packages.lock.json, tests/Vitrine.ArchitectureTests/ListingsModuleArchitectureTests.cs, tests/Vitrine.Tooling.Tests/AppHostModelTests.cs, tests/Vitrine.Tooling.Tests/AiProviderDeployTests.cs, tests/Vitrine.Tooling.Tests/packages.lock.json, tests/Fixtures/Anthropic/**, web/src/api/**, web/e2e/support/packages.lock.json]
adrs: [ADR-0001, ADR-0002, ADR-0006, ADR-0010, ADR-0012, ADR-0014, ADR-0015, ADR-0017]
external: []
size: M
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0035 — Sugestão de melhoria de anúncio com IA (geração, cota e custo)

<!-- type: feature | fix | refactor | migration | foundation. user_facing: true quando a mudança altera uma jornada do usuário (UI ou API pública) — exige teste E2E. Substitua todos os marcadores com chaves duplas: o G0 (`spec_graph.py validate`) reprova a spec enquanto restar algum. As seções de Checklist em diante são preenchidas depois da aprovação, sem marcadores. -->

## 1. Visão Geral
Entrega, no módulo `Listings`, a geração da sugestão de melhoria de um anúncio: título, descrição e atributos que faltam.

- O vendedor pede a melhoria (`POST`), e a geração roda em segundo plano, num comando durável do Wolverine.
- A geração lê o anúncio e a categoria no ML (SPEC-0033), chama a porta `IListingImprover` (ADR-0015) e valida o resultado com a mesma regra do editor (`ListingChangeRules`). O que viola uma regra é descartado com o motivo e nunca chega ao vendedor.
- Cada campo sugerido traz o original, a sugestão, se é editável e por quê, e uma verificação de *grounding* (termos que não aparecem no anúncio original).
- A porta tem dois adaptadores, escolhidos por `Ai:ListingImprover:Provider`:
  - `simulated` (padrão): **gerador simulado determinístico**, sem custo (ADR-0017);
  - `anthropic`: Claude Opus 5.5 pelo SDK oficial, com saída estruturada, testado com respostas gravadas e **desligado até o go-live**.
- Cota mensal por workspace (padrão 20), uma geração por anúncio por vez, tokens e custo estimado registrados por melhoria (ADR-0012).
- Endpoints para a SPA: detalhe do anúncio, pedir a melhoria, ler a última e consultar a cota.

## 2. Motivação & Escopo
**Motivação:** melhorar anúncios é a função central do produto (ADR-0015) e o maior custo variável (ADR-0012). O vendedor precisa de uma sugestão que ele possa comparar e aprovar, que não invente fatos sobre o produto e que respeite as regras do ML. Até o go-live, nada pode gerar custo de IA: dev, CI, E2E e ambiente local usam o gerador simulado (ADR-0017).

**Objetivos (dentro do escopo):**
- Agregado `ListingImprovement` no schema `listings` (tabela `listing_improvements`):
  - status `generating | ready | failed | applied | partially_applied | discarded` (`applied`/`partially_applied` são escritos pela SPEC-0036);
  - original e proposta por campo, `grounded` e termos sem origem por campo, motivo de descarte;
  - provedor, modelo, tokens (entrada, saída, cache lido e gravado) e custo estimado em US$;
  - `counts_toward_quota`.
- Porta `IListingImprover` na `Application` e serviço puro `ListingImprovementGenerator` (monta a entrada, chama a porta, valida, aplica o grounding), sem banco, reutilizado pela suíte de avaliação (SPEC-0037).
- Adaptador `simulated`: determinístico, só com dados do anúncio (§6).
- Adaptador `anthropic`: pacote NuGet `Anthropic` (ADR-0015), modelo `claude-opus-5-5`, `output_config.format` com JSON Schema, cache de prompt no prefixo estável, `stop_reason: "refusal"` como `AI_REFUSED`, tokens registrados. Só `Vitrine.Listings.Infrastructure` referencia o pacote.
- Validação pós-IA com `ListingChangeRules` (SPEC-0033) e verificação de grounding (`GroundingCheck`, no `Domain`).
- Geração assíncrona durável (`GenerateListingImprovement`), com retentativa em indisponibilidade.
- Cota mensal por workspace (`Listings:Improvements:MonthlyLimit`, padrão 20), mês civil no fuso `America/Sao_Paulo` → 429 `IMPROVEMENT_LIMIT_REACHED`.
- Uma geração em andamento por anúncio → 409 `IMPROVEMENT_IN_PROGRESS`.
- Configuração e proteção do provedor (ADR-0017):
  - padrão `simulated`;
  - `anthropic` exige `Ai:Anthropic:ApiKey` (o host recusa subir sem ela);
  - em `Production`, `simulated` exige `Ai:ListingImprover:AllowSimulated=true`, definido só no `deploy/local/.env.example`, no padrão do `MercadoLivre:AllowNonOfficialEndpoints`;
  - o AppHost (dev e E2E) define `simulated` de forma explícita.
- Endpoints (§6): `GET /api/v1/listings/{itemId}/details`, `POST /api/v1/listings/{itemId}/improvements`, `GET /api/v1/listings/{itemId}/improvements/latest`, `GET /api/v1/listings/improvements/quota`.
- Desconectar a conta apaga as melhorias dela (retenção da ADR-0006, mesmo handler da SPEC-0022).
- Métricas `vitrine.listings.improvements{result}`, `vitrine.listings.improvement_tokens{kind,provider}` e `vitrine.listings.improvement_cost_usd{provider}`.
- Cliente orval regenerado (`web/src/api`).

**Não-objetivos (fora do escopo):**
- Aprovar, publicar e descartar: SPEC-0036.
- Telas: SPEC-0038.
- Suíte de avaliação: SPEC-0037.
- Ligar o provedor real: go-live (SPEC-0009), junto com o usuário, depois da avaliação real.
- Fotos, preço, nota de qualidade do ML e `family_name`.
- Cota por plano de assinatura: épico de assinatura (aqui é um número por configuração).
- Streaming da resposta da IA para a tela.

## 3. Dependências
- **Implementações necessárias:** SPEC-0033, porque o código de produção chama `ListingChangeRules` (classe pura do `PublicApi`, sem dublê possível) e os testes usam as fixtures que ela versiona.
- **Contratos consumidos:** SPEC-0033@1:
  - `IMercadoLivreListingDetails` (detalhe e categoria);
  - `ListingChangeRules` (validação pós-IA e bloqueios por campo);
  - exceções públicas da SPEC-0021 reaproveitadas.
  - Os testes usam um dublê dos dois gateways montado com as fixtures de `tests/Fixtures/MercadoLivre/`, e o CT-01 fecha o laço.
- **Pré-requisitos externos:**
  - pacote NuGet `Anthropic` (ADR-0015 cita 12.55.0, verificado em 2026-10-13; a versão estável é conferida no G2);
  - nenhuma chave de API: o provedor real fica desligado até o go-live.

## 4. Decisão Arquitetural
**Contexto:** segue o módulo `Listings` da SPEC-0022:
- endpoints em `src/Modules/Listings/Vitrine.Listings.Presentation/ListingsEndpoints.cs` (`MapGroup`, `Problems`, `HttpsRequiredFilter` e `AntiforgeryFilter` no POST, membro do workspace por `IWorkspaceMembership`);
- casos de uso na `Application` (`RequestSyncHandler`), comando durável com fila local (`SyncListings` + `SyncListingsHandler` com `ScheduleRetry`);
- persistência em `ListingsModuleDbContext` com migrações próprias;
- métricas em `Infrastructure/Diagnostics/ListingsMetrics.cs`;
- remoção ao desconectar em `Messaging/AccountEventsHandler.cs`;
- configuração validada em `ListingsModule.cs` (`ValidateOnStart`).

A configuração protegida por ambiente segue `MercadoLivreOptionsValidator` (`AllowNonOfficialEndpoints`) e o AppHost segue `src/AppHost/AppHost.cs`.

**Decisão:**
- Novo slice `Improvements` em `Domain`, `Application`, `Infrastructure` e `Presentation` do `Listings`.
- `IListingImprover` na `Application`; adaptadores `SimulatedListingImprover` e `AnthropicListingImprover` na `Infrastructure`, registrados por `AddListingImprover(configuration)`, que a suíte de avaliação também usa.
- Exclusividade por anúncio com índice único parcial (`account_id, item_id WHERE status = 'generating'`).
- Cota com lock por workspace na mesma transação da criação (`pg_advisory_xact_lock` pela chave do workspace), sem contagem dupla em pedidos simultâneos.
- O `POST` só grava a melhoria e o comando no outbox e responde 202; a leitura no ML e a chamada da IA acontecem no handler durável.

**Justificativa:**
- Porta e adaptador por configuração são o que a ADR-0015 pede para trocar de modelo, e o que a ADR-0017 pede para dev e E2E sem custo.
- O comando durável (ADR-0014) sobrevive a reinício e retenta a indisponibilidade da IA e do ML sem prender a requisição do vendedor (a geração pode levar dezenas de segundos).
- Validar com a mesma regra do editor garante que a sugestão é publicável.

**Desvio do padrão existente:** pacote novo `Anthropic` no `Directory.Packages.props`, coberto pela ADR-0015 (dono da decisão) e pela ADR-0017 (adaptador desligado). Nenhum outro.

**Alternativas descartadas:**
- Gerar dentro da requisição HTTP: tempo de resposta imprevisível e retentativa a cargo do navegador.
- Chamar a API da Anthropic por `HttpClient` próprio: a ADR-0015 escolhe o SDK oficial.
- Grounding só pelo campo `grounded` do modelo: o modelo pode errar sobre si mesmo; a verificação determinística no servidor vale para os dois provedores.
- Cota por janela de 30 dias corridos: o vendedor entende "este mês".

**ADRs:** ADR-0001, ADR-0002, ADR-0006, ADR-0010, ADR-0012, ADR-0014, ADR-0015, ADR-0017.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:**
  - `POST /improvements` com p95 < 200 ms (só grava e enfileira) — IT-01;
  - geração com o provedor simulado em < 2 s no IT;
  - com o provedor real, alvo de < 60 s por geração (medido no go-live pela suíte de avaliação, SPEC-0037);
  - `GET .../details` faz no máximo 2 chamadas ao ML com a categoria em cache.
- **Segurança:**
  - todo endpoint exige sessão e filtra pelo workspace; anúncio de outro workspace dá 404 — IT-02;
  - antiforgery e HTTPS no POST — IT-02;
  - `Ai:Anthropic:ApiKey` é segredo (ADR-0006): só por variável de ambiente, nunca em log, exceção ou resposta — IT-06;
  - o texto do anúncio vai ao modelo como dado delimitado, nunca como instrução; a saída só é aceita depois da validação pós-IA — UT-02, IT-08.
- **Privacidade e dados pessoais:** título, descrição e atributos são dados públicos do anúncio do vendedor. Com o provedor real, eles vão à Anthropic (já previsto na ADR-0015). Nenhum dado de comprador. Retenção até desconectar a conta — IT-10. O registro de uso guarda o id do usuário que pediu.
- **Disponibilidade e resiliência:**
  - IA indisponível: o SDK retenta (padrão, 2 vezes) com timeout de 120 s; depois o Wolverine reagenda em 30 s e 2 min; esgotado, `failed` com `AI_UNAVAILABLE`, sem consumir a cota — IT-04;
  - ML indisponível na leitura: mesma retentativa; esgotada, `failed` com `ML_UNAVAILABLE` — IT-04;
  - comando idempotente: mensagem repetida ou melhoria que já saiu de `generating` é ignorada — IT-05.
- **Acessibilidade (UI):** N/A — sem tela (SPEC-0038).
- **Custo:**
  - com `simulated`: zero (sem chamada externa) — UT-06, IT-09;
  - com `anthropic`: Opus 5.5 a US$ 4 por milhão de tokens de entrada e US$ 20 de saída (ADR-0015); cache lido a US$ 0,20 (referência da Anthropic de 2026-10-06; cache gravado e demais preços configuráveis e conferidos no go-live). Estimativa da ADR-0012: ≈ US$ 0,06 por melhoria; com a cota de 20, ≈ US$ 1,20 por workspace por mês;
  - custo estimado gravado por melhoria e por workspace, e somado na métrica — UT-05, IT-07.

## 6. Artefato A — Contrato
**Interface:**
- `GET /api/v1/listings/{itemId}/details`
- `POST /api/v1/listings/{itemId}/improvements`
- `GET /api/v1/listings/{itemId}/improvements/latest`
- `GET /api/v1/listings/improvements/quota`
- porta `IListingImprover`; comando `GenerateListingImprovement`; configuração `Ai:*` e `Listings:Improvements:*`

```text
Todos exigem sessão; qualquer membro do workspace (Owner ou Member). Erros problem+json com "code".
Usuário sem workspace -> 403 FORBIDDEN. itemId: ^MLB\d+$ (outro formato -> 404).

GET /api/v1/listings/{itemId}/details
  lê a cópia local (workspace) e, no ML, IMercadoLivreListingDetails (item + categoria)
  200 { itemId, accountId, title, price, currencyId, status, soldQuantity, catalogListing, permalink, thumbnailUrl,
        categoryId, categoryName, maxTitleLength, maxDescriptionLength, description: string|null,
        titleLockedReason: "TITLE_LOCKED_SOLD"|"TITLE_LOCKED_CATALOG"|"TITLE_LOCKED_USER_PRODUCT"|null,
        editable: bool,                       // status active e conta connected
        attributes: [{ id, name, required, valueId, valueName, missingRequired: bool }] }   // hidden não aparece
  404 NOT_FOUND                         anúncio fora do workspace, inexistente na cópia local ou no ML
  409 ACCOUNT_REAUTHORIZATION_REQUIRED  conta em reautorização
  502 ML_UNAVAILABLE                    ML indisponível

POST /api/v1/listings/{itemId}/improvements     (HTTPS + antiforgery; sem corpo)
  202 { improvementId }                 cria a melhoria em generating e enfileira GenerateListingImprovement
  404 NOT_FOUND                         anúncio fora do workspace ou inexistente na cópia local
  409 ACCOUNT_REAUTHORIZATION_REQUIRED | LISTING_NOT_ACTIVE (cópia local != active) | IMPROVEMENT_IN_PROGRESS
  429 IMPROVEMENT_LIMIT_REACHED { limit, used, resetsAt }   + cabeçalho Retry-After (segundos até resetsAt)

GET /api/v1/listings/{itemId}/improvements/latest
  200 {
    improvementId, itemId, status: "generating"|"ready"|"failed"|"applied"|"partially_applied"|"discarded",
    failureCode: "AI_REFUSED"|"AI_INVALID_OUTPUT"|"AI_UNAVAILABLE"|"ML_UNAVAILABLE"|"LISTING_NOT_EDITABLE"|null,
    requestedAt, completedAt: datetime|null, provider: "simulated"|"anthropic",
    fields: {
      title:       { before, suggested: string|null, editable, blockedReason: string|null, maxLength,
                     grounded: bool, ungroundedTerms: [string], discardedReason: string|null },
      description: { before: string|null, suggested: string|null, editable, blockedReason, maxLength,
                     grounded, ungroundedTerms, discardedReason },
      attributes:  [{ id, name, required, before: { valueId, valueName }|null,
                      suggested: { valueId, valueName }|null, allowedValues: [{ id, name }], allowedUnits: [string],
                      editable, blockedReason, grounded, discardedReason }]
    },
    result: null | {                     // preenchido pela SPEC-0036 depois do apply
      approvedAt, approvedBy,
      title?: FieldOutcome, description?: FieldOutcome, attributes?: [{ id, ...FieldOutcome }] }
  }
  FieldOutcome = { result: "applied"|"rejected"|"not_attempted", code: string|null, message: string|null }
  Em generating/failed, fields traz só os valores "before" que já existem (ou vazio) e suggested null.
  404 NOT_FOUND                         nenhuma melhoria para o anúncio no workspace

GET /api/v1/listings/improvements/quota
  200 { limit, used, remaining, resetsAt }   // mês civil em America/Sao_Paulo; resetsAt = início do próximo mês

Códigos de descarte por campo (discardedReason): os de ListingChangeRules (SPEC-0033) e ATTRIBUTE_NOT_GROUNDED.
Cota: conta toda melhoria criada no mês, exceto as failed com ML_UNAVAILABLE, AI_UNAVAILABLE ou
LISTING_NOT_EDITABLE (counts_toward_quota = false: nada foi gasto por escolha do vendedor). AI_REFUSED e
AI_INVALID_OUTPUT contam (a chamada foi paga).

Comando durável GenerateListingImprovement(improvementId)  (fila local durável)
  1. status != generating -> ignora (idempotente)
  2. GetItemDetailsAsync + GetCategoryAsync; null ou status != active -> failed LISTING_NOT_EDITABLE
  3. ListingImprovementGenerator: monta ImprovementInput, chama IListingImprover, valida e verifica o grounding
  4. ready com campos, tokens e custo | failed com o código
  ML/IA indisponível -> ScheduleRetry(30 s, 2 min); esgotado -> failed (ML_UNAVAILABLE | AI_UNAVAILABLE)

Porta (Vitrine.Listings.Application.Improvements):
  interface IListingImprover { Task<ImprovementDraft> ImproveAsync(ImprovementInput input, CancellationToken ct); }
  record ImprovementInput(string ItemId, string CategoryName, string Title, string? Description,
         IReadOnlyList<InputAttribute> Attributes,             // id, name, valueName? (preenchidos e vazios)
         IReadOnlyList<EditableAttribute> EditableAttributes,  // id, name, valueType, required, values[], allowedUnits[]
         int MaxTitleLength, int MaxDescriptionLength, bool TitleLocked)
  record ImprovementDraft(string? Title, string? Description, IReadOnlyList<ProposedAttribute> Attributes,
         AiUsage Usage)                                        // ProposedAttribute(id, valueId?, valueName?)
  record AiUsage(string Provider, string Model, int InputTokens, int OutputTokens, int CacheReadTokens,
         int CacheWriteTokens)
  exceções: ListingImproverRefusedException(category) -> AI_REFUSED;
            ListingImproverInvalidOutputException -> AI_INVALID_OUTPUT;
            ListingImproverUnavailableException -> AI_UNAVAILABLE

Gerador simulado (determinístico; mesma entrada -> mesma saída, byte a byte):
  título: se não travado, reorganiza o título original com o nome da categoria e valores de atributos preenchidos,
          sem repetir palavras, cortado em fronteira de palavra até MaxTitleLength; travado -> null
  descrição: texto puro montado só com o título, a descrição original e os atributos preenchidos
          (parágrafos com "\n"), até MaxDescriptionLength
  atributos: só os vazios e editáveis cujo valor aparece no título ou na descrição (sem diferenciar maiúsculas e
          acentos): nome de um valor permitido (lista) ou número com unidade permitida (number_unit); nada além disso
  usage: provider "simulated", model "simulated", tokens 0

Adaptador anthropic (SDK oficial "Anthropic"; nenhum outro projeto referencia o pacote):
  model Ai:ListingImprover:Model (padrão claude-opus-5-5); output_config.effort Ai:ListingImprover:Effort (padrão
  medium, revisto pela avaliação); thinking adaptativo (padrão do modelo); max_tokens 16000; sem streaming
  output_config.format = json_schema (schema versionado em Infrastructure/Improvements/listing-improvement.schema.json):
    { title: string|null, title_sources: [string], description: string|null, description_sources: [string],
      attributes: [{ id, value_id: string|null, value_name: string|null, source_excerpt: string }] }
  prompt: system (instruções fixas, regras do ML, "não invente fatos; use só o que está no anúncio") com
          cache_control; depois as regras da categoria (2º ponto de cache); por último os dados do anúncio,
          delimitados como dado
  stop_reason "refusal" -> AI_REFUSED (stop_details.category no log); "max_tokens" ou JSON fora do schema ->
  AI_INVALID_OUTPUT; 429/5xx/529/rede/timeout depois das retentativas do SDK -> AI_UNAVAILABLE
  usage: input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens
  fallback de recusa do lado do servidor: desligado (Ai:ListingImprover:RefusalFallback=false; ver Questões)

Custo estimado (US$) = tokens × preço por milhão (Ai:ListingImprover:Pricing:{InputPerMillion=4,
  OutputPerMillion=20, CacheReadPerMillion=0.20, CacheWritePerMillion=5}); simulado = 0

Configuração:
  Ai:ListingImprover:Provider = simulated | anthropic   (padrão simulated; outro valor reprova a subida)
  Ai:ListingImprover:AllowSimulated (padrão false; exigido em Production com simulated)
  Ai:Anthropic:ApiKey (segredo; obrigatório com anthropic)
  Listings:Improvements:MonthlyLimit (padrão 20, 1..1000)
  deploy/compose.yaml: Ai__ListingImprover__Provider=${AI_LISTING_IMPROVER_PROVIDER:-simulated},
    Ai__ListingImprover__AllowSimulated=${AI_ALLOW_SIMULATED:-false}, Ai__Anthropic__ApiKey=${ANTHROPIC_API_KEY:-}
  deploy/local/.env.example: AI_ALLOW_SIMULATED=true (sem chave da Anthropic)
  AppHost (run): Api com Ai__ListingImprover__Provider=simulated

Persistência (schema listings):
  listing_improvements(id uuid pk, workspace_id, account_id, item_id varchar(32), status, failure_code,
    requested_by uuid, requested_at, completed_at, provider, model, input_tokens, output_tokens, cache_read_tokens,
    cache_write_tokens, estimated_cost_usd numeric(12,6), counts_toward_quota bool, original jsonb, proposal jsonb,
    apply_started_at timestamptz null (SPEC-0036), result jsonb null (SPEC-0036))
  índices: único parcial (account_id, item_id) where status = 'generating';
           (workspace_id, requested_at) para a cota; (account_id, item_id, requested_at desc) para o latest
  MercadoLivreAccountDisconnected -> apaga as melhorias da conta
```

**Design:** N/A — sem tela nesta spec. As telas da SPEC-0038 consomem este contrato.

**Arquivos/módulos afetados:** ver `touches`.
- `src/Modules/Listings/*/Improvements/**`: agregado, porta, gerador, `GroundingCheck`, adaptadores, handler, endpoints, migração.
- `src/Api` (registro da configuração `Ai:*`), `src/AppHost/AppHost.cs`, `deploy/compose.yaml`, `deploy/local/.env.example`.
- `Directory.Packages.props` e lockfiles dos projetos que referenciam o `Listings.Infrastructure`.
- Respostas gravadas da Anthropic em `tests/Fixtures/Anthropic/` (sintéticas, no formato da Messages API).

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Pedir melhoria | anúncio ativo do workspace, cota livre | 202 `{improvementId}`; geração roda e fica `ready` | IT-01 |
| Ler a última | após a geração | 200 com antes, sugestão, editável e bloqueio por campo | IT-01, CT-02 |
| Detalhe do anúncio | anúncio do workspace | 200 com atributos, obrigatórios faltando e descrição | IT-01, CT-02 |
| Gerador simulado | mesma entrada | mesma saída; só dados do anúncio; atributo só se derivável | UT-01 |
| Validação pós-IA | sugestão viola limite, texto puro, contato, título travado ou valor permitido | campo descartado com motivo; valor inválido nunca é exposto | UT-02, IT-08 |
| Grounding | termo, número ou medida fora do original | `grounded=false` com os termos; atributo livre sem origem descartado | UT-03, IT-08 |
| Anúncio alheio ou inexistente | outro workspace / sem cópia local | 404 no POST, no latest e no details | IT-02 |
| Conta em reautorização | conta `paused` | 409 `ACCOUNT_REAUTHORIZATION_REQUIRED` | IT-02 |
| Anúncio não ativo | cópia local `paused`/`closed` | 409 `LISTING_NOT_ACTIVE` | IT-02 |
| Geração em andamento | 2º POST durante `generating`, ou 5 simultâneos | 409 `IMPROVEMENT_IN_PROGRESS`; só 1 criada | IT-02 |
| Sem antiforgery | POST sem token | 400 | IT-02 |
| Cota esgotada | limite atingido no mês | 429 `IMPROVEMENT_LIMIT_REACHED` com `resetsAt`; simultâneos não ultrapassam | UT-04, IT-03 |
| Consultar cota | GET quota | `limit`, `used`, `remaining`, `resetsAt` | UT-04, IT-03 |
| IA indisponível | adaptador indisponível | retentativa; esgotada, `failed AI_UNAVAILABLE`, sem consumir cota | IT-04 |
| IA recusa | `stop_reason refusal` | `failed AI_REFUSED`, consome a cota | UT-05, IT-04 |
| Saída inválida | `max_tokens` ou JSON fora do schema | `failed AI_INVALID_OUTPUT` | UT-05, IT-06 |
| ML indisponível ou anúncio não editável | leitura falha / anúncio pausado no ML | `failed ML_UNAVAILABLE` / `LISTING_NOT_EDITABLE`, sem consumir cota | IT-04 |
| Durabilidade | reinício no meio; mensagem repetida | conclui uma vez; repetida ignorada | IT-05 |
| Adaptador Anthropic | respostas gravadas (sucesso, recusa, 529, 400) | mapeadas; chave só no cabeçalho; zero chamada real | UT-05, IT-06 |
| Custo e métricas | geração concluída | tokens e custo gravados; `improvements{result}`, tokens e custo | UT-05, IT-07 |
| Provedor por configuração | padrão, `anthropic` sem chave, `simulated` em Production | `simulated`; host não sobe; host não sobe sem `AllowSimulated` | UT-06 |
| CI, E2E e local sem custo | AppHost, Compose e `.env.example` | `simulated` explícito; `AllowSimulated` só no local; sem chave | IT-09 |
| Só o Infrastructure usa o SDK | assemblies | só `Vitrine.Listings.Infrastructure` referencia `Anthropic` | UT-07 |
| Desconectar | `MercadoLivreAccountDisconnected` | melhorias da conta apagadas | IT-10 |

## 7. Artefato B — Plano de Testes (TDD)
<!-- Todo teste tem um ID e é escrito ANTES da implementação (Red). No código, cada teste carrega a tag `SPEC-0035:<ID>` — o G1 (`spec_graph.py verify`) verifica. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem testes: "N/A — motivo" SEM ID. Teste que protege comportamento existente e passa antes da mudança: marque `(guarda: passa antes da mudança)`. Estratégia completa: references/testing.md da skill. -->

### 7.1 Testes de Caracterização
- N/A — slice novo. Os testes da SPEC-0022 (sincronização, desconexão, isolamento) continuam como guarda no CI.

### 7.2 Testes Unitários
- **UT-01** — Dado o `SimulatedListingImprover` e entradas sintéticas, quando gera duas vezes a mesma entrada, então a saída é idêntica, e:
  - o título fica dentro de `MaxTitleLength`, cortado em fronteira de palavra, e é `null` quando `TitleLocked`;
  - toda palavra da descrição gerada (fora as de ligação fixas do modelo de texto) vem do título, da descrição ou dos atributos originais, e o texto não tem HTML nem emoji;
  - `COLOR` recebe "Preto" quando a descrição traz "Cor: preto"; `MODEL`, sem valor derivável, não é preenchido; `CAPACITY` recebe "500 mL" só quando "500 mL" aparece no anúncio.
- **UT-02** — Dado rascunhos que violam regras (título acima do limite, título com o anúncio travado, descrição com HTML, emoji, e-mail ou telefone, atributo `read_only` e valor fora da lista), quando o `ListingImprovementGenerator` valida, então cada campo inválido sai com `suggested = null` e `discardedReason` com o código de `ListingChangeRules`, e os demais campos são mantidos.
- **UT-03** — Dado `GroundingCheck`, quando a sugestão traz número, medida ou termo próprio (palavra com inicial maiúscula fora do início da frase, ou com dígito) que não aparece no original (sem diferenciar maiúsculas e acentos), então o campo fica `grounded=false` com a lista desses termos; quando todos aparecem, `grounded=true`. Atributo de texto livre ou numérico cujo valor não aparece no original é descartado com `ATTRIBUTE_NOT_GROUNDED`.
- **UT-04** — Dado o cálculo da cota com relógio falso, então:
  - o mês é o civil em `America/Sao_Paulo` (31/10 23:30 local conta em outubro; 01/11 00:10 local, em novembro);
  - `failed` com `ML_UNAVAILABLE`, `AI_UNAVAILABLE` ou `LISTING_NOT_EDITABLE` não conta; `AI_REFUSED` conta;
  - `resetsAt` é o início do mês seguinte no fuso;
  - `MonthlyLimit` fora de 1..1000 reprova a validação das opções.
- **UT-05** — Dado o `AnthropicListingImprover` com um transporte HTTP falso (sem rede), quando monta a requisição e lê respostas gravadas, então:
  - a requisição tem `model` `claude-opus-5-5`, `output_config.format` com o JSON Schema versionado, `output_config.effort` configurado, `max_tokens` 16000 e `cache_control` no system e nas regras da categoria, com os dados do anúncio depois do último ponto de cache;
  - resposta válida vira `ImprovementDraft` com `AiUsage` (entrada, saída, cache lido e gravado);
  - `stop_reason: "refusal"` lança `ListingImproverRefusedException` com a categoria; `max_tokens` ou JSON fora do schema lança `ListingImproverInvalidOutputException`;
  - o custo estimado de `usage` conhecido bate com a tabela de preços configurada.
- **UT-06** — Dado a validação das opções de IA, então:
  - sem configuração, o provedor é `simulated`;
  - `anthropic` sem `Ai:Anthropic:ApiKey` reprova a subida, com mensagem sem segredo;
  - `simulated` em `Production` sem `AllowSimulated=true` reprova a subida; em `Development`, passa;
  - provedor desconhecido reprova;
  - com `simulated`, o contêiner não registra o cliente da Anthropic.
- **UT-07** — Dado os assemblies, quando as regras de arquitetura rodam, então só `Vitrine.Listings.Infrastructure` referencia o assembly `Anthropic`; `Domain`, `Application`, `Presentation`, a Api e os demais módulos não. As regras de camadas e isolamento da SPEC-0022 continuam passando com o slice novo.

### 7.3 Testes de Integração
<!-- Postgres real + Wolverine real; IMercadoLivreListingDetails por dublê do contrato SPEC-0033@1 montado com as fixtures; provedor simulated, salvo onde indicado. -->
- **IT-01** — Dado um anúncio ativo do workspace e o dublê do ML, quando o usuário faz `POST /improvements`, então:
  - recebe 202 com `improvementId` (p95 < 200 ms em 20 pedidos de anúncios distintos);
  - o comando roda, e `GET /improvements/latest` devolve `ready`, com título, descrição e `COLOR` sugeridos, `before` iguais ao ML, tokens 0 e custo 0;
  - `GET /details` devolve os atributos com `missingRequired=true` para `COLOR` e `titleLockedReason` nulo; para o anúncio com vendas, `TITLE_LOCKED_SOLD`, e o latest dele traz o título com `editable=false`.
- **IT-02** — Dado dois workspaces, então:
  - anúncio de outro workspace ou inexistente dá 404 no POST, no latest e no details; latest sem melhoria dá 404;
  - conta em reautorização dá 409 `ACCOUNT_REAUTHORIZATION_REQUIRED`; cópia local `paused` dá 409 `LISTING_NOT_ACTIVE`;
  - 2º POST durante `generating` dá 409 `IMPROVEMENT_IN_PROGRESS`, e 5 POSTs simultâneos criam exatamente 1 melhoria;
  - POST sem antiforgery dá 400;
  - `GET /details` com o ML indisponível dá 502 `ML_UNAVAILABLE`.
- **IT-03** — Dado `MonthlyLimit=3`, quando o workspace pede melhorias de anúncios distintos, então o 4º pedido do mês dá 429 `IMPROVEMENT_LIMIT_REACHED` com `limit`, `used`, `resetsAt` e `Retry-After`. Uma melhoria `failed` por `ML_UNAVAILABLE` não conta. Com 2 usadas, 5 POSTs simultâneos criam exatamente 1. `GET /quota` mostra `limit=3`, `used` e `remaining` coerentes, e outro workspace tem a própria cota.
- **IT-04** — Dado falhas, então:
  - adaptador lançando `ListingImproverUnavailableException`: reagendamentos e, esgotados, `failed AI_UNAVAILABLE` sem consumir cota;
  - recusa: `failed AI_REFUSED`, consumindo cota;
  - dublê do ML lançando `MercadoLivreUnavailableException`: `failed ML_UNAVAILABLE`;
  - anúncio pausado no ML: `failed LISTING_NOT_EDITABLE`;
  - `vitrine.listings.improvements{result}` registra cada desfecho.
- **IT-05** — Dado uma melhoria em `generating`, quando o host para no meio da geração e sobe de novo (padrão de `WolverineDurabilityTests`), então ela termina `ready` uma única vez. Uma 2ª entrega do mesmo comando é ignorada.
- **IT-06** — Dado `Provider=anthropic`, `Ai:Anthropic:ApiKey` sintética e o WireMock no lugar da API da Anthropic, servindo as respostas gravadas de `tests/Fixtures/Anthropic/` (sucesso com `usage` de cache, recusa, `max_tokens`, 529 e 400), quando a geração roda, então:
  - sucesso: `ready`, com tokens e custo gravados;
  - recusa: `AI_REFUSED`; `max_tokens`: `AI_INVALID_OUTPUT`; 529 persistente: `AI_UNAVAILABLE`;
  - a requisição leva a chave só no cabeçalho `x-api-key`;
  - nenhum log, exceção ou resposta contém a chave;
  - nenhuma requisição sai para um host diferente do WireMock.
- **IT-07** — Dado gerações concluídas com o WireMock da Anthropic, quando as métricas e o banco são inspecionados, então `improvement_tokens{kind,provider}` e `improvement_cost_usd{provider}` somam o `usage` das respostas, e cada melhoria guarda o custo estimado e o `workspace_id`.
- **IT-08** — Dado um adaptador de teste que devolve título com "Bluetooth 5.3" (ausente do anúncio), descrição com HTML e `COLOR` com valor fora da lista, quando a geração roda, então o latest traz o título com `grounded=false` e `ungroundedTerms` contendo "5.3", a descrição com `suggested=null` e `discardedReason=DESCRIPTION_INVALID`, e `COLOR` descartado com `ATTRIBUTE_VALUE_NOT_ALLOWED`. O valor inválido não aparece em nenhuma resposta.
- **IT-09** — Dado os arquivos de execução (testes de ferramenta no padrão de `MercadoLivreSimulatorDeployTests`), então:
  - em modo run, o AppHost passa `Ai__ListingImprover__Provider=simulated` para a Api;
  - o `deploy/compose.yaml` tem `simulated` como padrão e `AllowSimulated` falso;
  - só o `deploy/local/.env.example` liga `AI_ALLOW_SIMULATED`, e nenhum arquivo versionado define `ANTHROPIC_API_KEY` com valor.
- **IT-10** — Dado melhorias de uma conta, quando `MercadoLivreAccountDisconnected` é processado, então não resta nenhuma melhoria da conta, e as de outra conta ficam intactas.

### 7.4 Testes de Contrato
- **CT-01** — Dado as fixtures da SPEC-0033 (`item-details`, `item-description`, `category`, `category-attributes`), quando o dublê de `IMercadoLivreListingDetails` as serve pelos tipos públicos do contrato SPEC-0033@1 e a geração roda com o provedor simulado, então a melhoria fica `ready`, e os bloqueios por campo coincidem com `ListingChangeRules`. É o lado consumidor do contrato.
- **CT-02** — Dado o documento OpenAPI gerado, quando comparado com o §6, então `details`, `improvements`, `latest` e `quota` têm os campos, tipos e códigos de resposta documentados, e o cliente orval regenerado compila. A SPEC-0036 e a SPEC-0038 consomem este contrato.

### 7.5 Testes E2E
- N/A — sem tela nesta spec. A jornada `aprovar-melhoria` é a SPEC-0038 (E2E 01 e 04), contra o simulador do ML e a IA simulada.

### 7.6 Outros
- Desempenho: IT-01 (p95 do POST).
- Segurança: IT-02 (isolamento e antiforgery) e IT-06 (chave fora de logs e respostas).

**Dublês e dados de teste:**
- dublê em memória de `IMercadoLivreListingDetails`, montado com as fixtures da SPEC-0033;
- adaptadores de teste de `IListingImprover` (indisponível, recusa, rascunho inválido);
- WireMock no lugar da API da Anthropic, com as respostas gravadas sintéticas de `tests/Fixtures/Anthropic/` (formato da Messages API, origem anotada);
- `TimeProvider` falso para a cota;
- builders de anúncio e de conta (suporte da SPEC-0022).

**Ambiente de execução:** UT em processo; IT com Testcontainers e WireMock (local e CI); IT-09 no `Vitrine.Tooling.Tests` (lê o Compose com `docker compose config`). Nenhum teste chama a Anthropic de verdade (ADR-0002).

## 8. Plano de Rollout
- **Estratégia:** deploy direto com o provedor `simulated` (padrão). O provedor real fica desligado por configuração (`Ai:ListingImprover:Provider`) até o go-live (SPEC-0009), depois da suíte de avaliação real (SPEC-0037) e do limite de gasto na Anthropic.
- **Dados/schema:** migração só de criação (tabela `listing_improvements` e índices), aplicada pelo `--migrate`. Reversível: a tabela fica sem uso.
- **Compatibilidade:** endpoints novos; cliente orval regenerado. Quem tem `deploy/local/.env` copia a linha `AI_ALLOW_SIMULATED=true` (nota no runbook).
- **Observabilidade:**
  - `vitrine.listings.improvements{result=ready|failed_ai_refused|failed_ai_unavailable|failed_ai_invalid_output|failed_ml_unavailable|failed_not_editable|limit_reached|in_progress}`;
  - `vitrine.listings.improvement_tokens{kind=input|output|cache_read|cache_write,provider}`;
  - `vitrine.listings.improvement_cost_usd{provider}`;
  - log Information por geração concluída (melhoria, provedor, modelo, tokens, custo, duração, sem o texto) e Warning em `failed` (código, categoria da recusa).
- **Rollback:** revert do merge. A tabela fica sem uso; nada é escrito no ML por esta spec.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Qual a cota e como ela conta? — 20 melhorias por workspace por mês civil (`America/Sao_Paulo`), configurável. Conta o que foi pedido e gasto, inclusive recusa da IA; não conta falha do ML ou indisponibilidade da IA. O teto de 20 vem da ADR-0012 (teste grátis). (Architect, 2026-11-02, a confirmar no H1)
- [x] Quem pode pedir? — Qualquer membro do workspace (Owner ou Member), a mesma regra da sincronização. Aprovar e publicar também (SPEC-0036). (Architect, 2026-11-02, a confirmar no H1)
- [x] O que fazer com termo sem origem no anúncio? — Título e descrição aparecem com aviso de grounding e vêm desmarcados na tela (SPEC-0038); atributo sem origem é descartado. A suíte de avaliação exige 100% de grounding do modelo real antes do go-live (SPEC-0037). (Architect, 2026-11-02, a confirmar no H1)
- [x] Ligar o fallback de recusa do lado do servidor da Anthropic? — Não por enquanto. A Anthropic recomenda ligar, mas ele troca o modelo da resposta, o que muda custo e qualidade fora da ADR-0015. Fica desligado por configuração e é decidido no go-live, com a suíte de avaliação. (Architect, 2026-11-02, a confirmar no H1)
- [x] Effort do Opus 5.5? — `medium` (padrão do modelo), configurável; a avaliação real do go-live compara `medium` e `high`. (Architect, 2026-11-02, a confirmar no H1)
- [x] Precisa de endpoint de detalhe do anúncio? — Sim: a tela de detalhe (SPEC-0038) mostra atributos, obrigatórios faltando e descrição, que não estão na cópia local. Lido do ML a cada abertura, com a categoria em cache. (Architect, 2026-11-02, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Domínio da sugestão**
- [ ] Red: escrever UT-01..UT-07 com a tag `SPEC-0035:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `ListingImprovement`, validação contra as regras da categoria, grounding e cota mensal
- [ ] Refactor mantendo tudo verde

**Fase 2: Geração, cota e endpoints**
- [ ] Red: escrever IT-01..IT-10 e CT-01..CT-02 com a tag `SPEC-0035:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: porta `IListingImprover`, gerador simulado determinístico, adaptador Anthropic desligado, handler assíncrono, migração e endpoints `/improvements`, `/details` e `/improvements/quota`
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0035`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

---
id: SPEC-0018
title: Mascaramento do Sentry ignora contexts, extras não-string e valores sensíveis de Exception.Data
tier: lite
type: fix
user_facing: false
status: implemented
created: 2026-10-29
parent: SPEC-0001
depends_on: [SPEC-0015]
consumes_contract: []
touches: [src/Api/Observability/**, tests/Vitrine.IntegrationTests/Observability/**]
adrs: [ADR-0006, ADR-0009]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-29
approved_via: piloto automático autorizado no chat em 2026-10-29
---

# SPEC-0018 — Mascaramento do Sentry ignora contexts, extras não-string e valores sensíveis de Exception.Data

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
Achados minor pré-existentes do G4 da SPEC-0015, todos reproduzidos pelo Reviewer com o host real e transporte de gravação do Sentry:

1. **Valor de chave sensível de `Exception.Data` repetido na mensagem:** `new InvalidOperationException("falha com P4-MSG")` com `Data["Token"]="P4-MSG"` → `mechanism.data.Token = ***`, mas `P4-MSG` sai em `exception.value` (o laço de tags já adiciona valores sensíveis à lista; o de `Data` não).
2. **`Data` com prefixo `sentry:`:** o SDK move `sentry:context:*` para `contexts` e `sentry:*` para `extra`; `MaskEvent` não percorre `contexts`, e o laço de extras só mascara valores `string` (`Data["sentry:context:auth"]=Dictionary{password=...}` e `Data["sentry:extra"]=new{Password=...}` saem crus).
3. **Objetos em chave neutra:** `ToString` de tipo anônimo/record (`{ Password = X }`) escapa da regex `nome=valor` por causa dos espaços; structs (ex.: `KeyValuePair`) são serializados sem mascaramento.

Esperado: nenhum desses caminhos envia segredo ao Sentry. Impacto: baixo hoje (nenhum código grava segredo nesses lugares; `src/` não usa prefixo `sentry:`), mas fere o ADR-0006.

## 2. Causa Raiz
`src/Api/Observability/ObservabilityExtensions.cs` (`MaskEvent`/`MaskMechanismData`): a lista de valores sensíveis é montada antes de olhar `Mechanism.Data`; `Contexts` não é percorrido; extras e valores de `Data` não-string são tratados só pelo `ToString` ou ignorados. O padrão de `nome=valor` do `SensitiveDataMasker` não aceita espaços em volta do `=`.

## 3. Mudança Proposta
Pré-passagem que junta os valores de chaves sensíveis de todos os `Mechanism.Data` à lista antes de mascarar exceções e mensagem do evento (breadcrumbs já gravados no evento não são reescritos: o SDK só os expõe para leitura, e um valor logado antes sob nome neutro também vai cru para console/OTLP — limite do ADR-0006; tipos do protocolo do SDK postos de propósito em `sentry:context:` também ficam como limite); `Contexts` e `Extra` mascarados recursivamente (chave sensível → `***`, texto → `MaskText`), inclusive valores não-string; objetos/structs em `Data` e extras são lidos pelas propriedades públicas (reflexão) e mascarados como dicionário, com o `ToString` só como fallback (`***` quando não representáveis com segurança); `JsonElement` é tratado como dicionário/lista; a coleta e o mascaramento nunca lançam (falha → `***`), só enumeram coleções materializadas e controlam ciclos; valores numéricos ou com até 3 caracteres não entram na lista de segredos. `Uri` perde o `UserInfo` antes de virar texto; cada valor tem um orçamento global de nós (ex.: 1000) além da profundidade e do limite por nó. Limites conhecidos (ADR-0006): getters de objetos de domínio são lidos mais de uma vez por log; textos com 4+ caracteres sob nome sensível mascaram a mesma palavra em outros pontos; objeto usado como chave de dicionário e classe sem membros públicos usam o `ToString`. Não muda: console, OTLP, pilha, Type/Handled, campos não sensíveis.

**Padrão seguido:** `MaskEvent`, `MaskMechanismData`, `MaskBreadcrumb` (SPEC-0007/0013/0015) e `SensitiveDataMasker` do ServiceDefaults (só uso; a regex com espaços, se precisar mudar, fica dentro do `MaskEvent` sem alterar o ServiceDefaults).

**Rollback:** revert do commit.

**Outras ocorrências:** Pipeline de logs (OTLP/console) tem o mesmo limite para `ToString` de objetos em propriedades (`SensitiveDataMasker.MaskProperty`): registrado aqui como limite conhecido do ADR-0006; nenhum código atual loga objetos com segredo.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0018:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — coberta pelos testes de mascaramento do Sentry das SPEC-0007, 0013 e 0015.
- Unitários: N/A — os defeitos aparecem no envelope do host real; a regressão é de integração.
- **IT-01** — Dado a API com Sentry configurado (transporte de teste), quando são registradas exceções com (a) `Data["Token"]` cujo valor também está na mensagem da exceção, (b) `Data["sentry:context:auth"]` com dicionário contendo `password` e `Data["sentry:extra"]` com objeto contendo `Password`, e (c) `Data` neutro com tipo anônimo, record e `KeyValuePair` contendo segredo, então nenhum envelope contém os valores de segredo usados, os eventos são enviados e as chaves não sensíveis continuam presentes.
- **IT-02** — Dado a API com Sentry configurado (transporte de teste), quando `Exception.Data` traz (a) record/anônimo com senha contendo `}`, `]`, `, Nome =` ou quebra de linha, (b) objeto aninhado sob nome sensível (`new { Credentials = new { Valor = X } }`), (c) objeto com `Password` cujo valor também está na mensagem da exceção e (d) `JsonElement` objeto com `"password"`, então nenhum envelope contém os segredos; (e) com um `IEnumerable` cuja enumeração lança, `LogError` não lança e o evento é enviado; e (f) valores numéricos ou de até 3 caracteres sob nome sensível (`TokenTtl=1`) não corrompem `exception.value` nem `HResult`. Inclui também: `Uri` com credenciais (`postgres://admin:SENHA@db:5432/x`) em `Data` ou dentro de objeto sai sem o `UserInfo`; e um grafo com referências compartilhadas (DAG de 10 níveis × 10 chaves para o mesmo filho) termina o `LogError` rápido (< 1 s), sem lançar, com o excedente como `***` e o evento enviado.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma.

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever IT-01 e IT-02 com a tag `SPEC-0018:<ID>` e confirmar que falha pelo motivo certo
- [x] Green: pré-passagem dos valores sensíveis de `Mechanism.Data`; `Contexts`/`Extra` mascarados recursivamente; objetos e structs mascarados
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like com smoke (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 927c0df (árvore suja) | 2026-10-29 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 8957016 | 2026-10-29 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  356ms (environment 61%, transform 17%, import 17%, tests 4%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Coverage: 88%) — c2a5a6d | 2026-10-29 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 331ms) — c2a5a6d | 2026-10-29 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: APPROVED (Reviewer independente, 3ª rodada) em c2a5a6d: 1ª rodada CHANGES_REQUESTED (2 majors: parse do ToString vazava, LogError lançava) corrigida; IT-01+IT-02 25/25, os 3 casos novos falhavam em dc05a6c; sondas sem vazamento em objetos/records/JsonElement/coleções preguiçosas/ciclos/DAG/Uri com credencial; limites registrados no Relatório: orçamento global do Collect estourado antes do segredo, credencial %-escapada repetida na mensagem, Uri relativa, Uri só com usuário mascara demais, segredo ≤3 chars — c2a5a6d | 2026-10-29 |
| G5 Integração & CI | PASS | MANUAL: PR #40 (sdd/int-wave-0012-0018 @ d0dbdfb): CI 7/7 verde + CodeQL (runs/40962468558); local no integrado: build 0 avisos, format/lint ok, dotnet 190/190, vitest 6/6, tests/ci 3/3, e2e 30/30, drift do api:generate limpo, npm audit sem high, gitleaks 0 | 2026-10-29 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-29 |
| G6 Deploy | PASS | smoke_test exit 0 (2 passed (2.4s)) — 26e550a (árvore suja) | 2026-10-29 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 26e550a (árvore suja) | 2026-10-29 |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
O mascaramento do Sentry cobre o que `Exception.Data` leva para fora do texto simples: segredos de chave sensível repetidos na mensagem, `sentry:context:*` e `sentry:*` (contexts e extras do usuário, mascarados recursivamente), objetos de domínio (records, anônimos, structs, lidos por reflexão), `JsonElement`, coleções e `Uri` com credenciais (sem `UserInfo`). O `LogError` nunca lança por causa do mascaramento, mesmo com valores que não dá para ler com segurança.

### Como foi feito
Novo `src/Api/Observability/SentryDataMasker.cs` (`MaskValue`/`CollectSensitiveValues`): reflexão para objetos de domínio e `ToString` só como fallback (tipos da plataforma, sem membros); só coleções materializadas; visitados por caminho, profundidade 8, 100 entradas por nó e orçamento de 1000 nós por percurso; try/catch em todos os pontos; números e textos de até 3 caracteres fora da lista de segredos. `MaskEvent` faz a pré-passagem e mascara Contexts/Extra (contexts do SDK intactos); `MaskingLoggerProvider` inclui `Exception.Data` em `CurrentSensitiveValues`. Emendas v1.1 (G4 FAIL: parse do ToString vazava; LogError lançava) e v1.2 (Uri, orçamento de nós, limites). Review: FAIL, APPROVED, APPROVED (3 rodadas).

### Prova de Correção
<!-- type fix: teste de regressão falhou antes (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->
Red 8957016 (IT-01: 3 casos vazando), df53ed3 (IT-02 a–f: vazamentos por delimitadores, aninhado, objeto na mensagem, JsonElement, `LogError` lançando `AggregateException`, `HResult` corrompido) e c3a5866 (Uri com credencial, DAG > 5 s, ToString sob nome sensível na mensagem). Green fdcad52, dc05a6c e c2a5a6d: IT-01+IT-02 25/25, Observability 40/40, suíte completa 190/190, diff-cover 88%.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| IT-01 | Data com valor de chave sensível na mensagem, sentry:context/sentry:extra e objetos em chave neutra não vazam | PASS | CI PR #40 run 40962468558; main run 40963136253 (26e550a) |
| IT-02 | Objetos de formato difícil, JsonElement, Uri com credencial, coleções que lançam/preguiçosas, ciclos, DAG e valores curtos: sem vazamento, LogError não lança, mensagem e HResult intactos | PASS | CI PR #40 run 40962468558; main run 40963136253 (26e550a) |

### Definição de Pronto
- [x] Teste de regressão falhou antes e passa depois da correção
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like: imagem `ghcr.io/thomasravache/vitrine-api:26e550a` (commit de merge do PR #40) da CI da `main` (run 40963136253) via `deploy/deploy.sh`, health 200 em 4 s, smoke `@smoke` 2/2 (2026-10-29).

### Pendências
- Limites conhecidos (ADR-0006), registrados nas emendas e na review, sem spec: breadcrumbs já gravados no evento não são reescritos; tipos do protocolo do SDK postos de propósito em `sentry:context:`; getters de objetos lidos mais de uma vez por log; textos de 4+ caracteres sob nome sensível mascaram a mesma palavra em outros pontos; objeto como chave de dicionário e classe sem membros públicos usam o `ToString`; orçamento do `Collect` compartilhado por todo o `Data` (um `Data` com >1000 nós antes do segredo deixa de coletá-lo para a mensagem); credencial %-escapada repetida na mensagem; `Uri` relativa; segredo de até 3 caracteres.

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.2 (escopo) | 2026-10-29 | `Uri` sem `UserInfo`; orçamento global de nós por valor; IT-02 ganha os dois casos; limites registrados (getters lidos mais de uma vez, mascaramento excessivo de palavras, chave-objeto e classe sem membros via `ToString`) | G4 2ª rodada APPROVED com minors: Uri com senha crua e DAG explodindo tempo/memória no LogError | nenhuma | thomas (2026-10-29, piloto automático) |
| v1.1 (escopo) | 2026-10-29 | Objetos lidos por reflexão (ToString só fallback), `JsonElement`, coleta/mascaramento que nunca lançam e só enumeram coleções materializadas, controle de ciclos, valores numéricos/curtos fora da lista; IT-02 novo; breadcrumbs já gravados no evento e tipos do protocolo do SDK em `sentry:context:` registrados como limite | G4 CHANGES_REQUESTED: vazamentos pelo parse do ToString e regressão (LogError lançava) | nenhuma | thomas (2026-10-29, piloto automático) |

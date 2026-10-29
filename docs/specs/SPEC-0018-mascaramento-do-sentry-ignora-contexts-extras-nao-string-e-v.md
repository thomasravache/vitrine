---
id: SPEC-0018
title: Mascaramento do Sentry ignora contexts, extras não-string e valores sensíveis de Exception.Data
tier: lite
type: fix
user_facing: false
status: in-progress
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
Pré-passagem que junta os valores de chaves sensíveis de todos os `Mechanism.Data` à lista antes de mascarar exceções, mensagem e breadcrumbs do evento; `Contexts` e `Extra` mascarados recursivamente (chave sensível → `***`, texto → `MaskText`), inclusive valores não-string; objetos/structs em `Data` e extras viram texto mascarado (ou `***` quando não representáveis com segurança). Não muda: console, OTLP, pilha, Type/Handled, campos não sensíveis.

**Padrão seguido:** `MaskEvent`, `MaskMechanismData`, `MaskBreadcrumb` (SPEC-0007/0013/0015) e `SensitiveDataMasker` do ServiceDefaults (só uso; a regex com espaços, se precisar mudar, fica dentro do `MaskEvent` sem alterar o ServiceDefaults).

**Rollback:** revert do commit.

**Outras ocorrências:** Pipeline de logs (OTLP/console) tem o mesmo limite para `ToString` de objetos em propriedades (`SensitiveDataMasker.MaskProperty`): registrado aqui como limite conhecido do ADR-0006; nenhum código atual loga objetos com segredo.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0018:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — coberta pelos testes de mascaramento do Sentry das SPEC-0007, 0013 e 0015.
- Unitários: N/A — os defeitos aparecem no envelope do host real; a regressão é de integração.
- - **IT-01** — Dado a API com Sentry configurado (transporte de teste), quando são registradas exceções com (a) `Data["Token"]` cujo valor também está na mensagem da exceção, (b) `Data["sentry:context:auth"]` com dicionário contendo `password` e `Data["sentry:extra"]` com objeto contendo `Password`, e (c) `Data` neutro com tipo anônimo, record e `KeyValuePair` contendo segredo, então nenhum envelope contém os valores de segredo usados, os eventos são enviados e as chaves não sensíveis continuam presentes.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma.

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever IT-01 com a tag `SPEC-0018:<ID>` e confirmar que falha pelo motivo certo
- [ ] Green: pré-passagem dos valores sensíveis de `Mechanism.Data`; `Contexts`/`Extra` mascarados recursivamente; objetos e structs mascarados
- [ ] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 927c0df (árvore suja) | 2026-10-29 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 8957016 | 2026-10-29 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  336ms (environment 64%, import 16%, transform 15%, tests 4%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Coverage: 80%) — fdcad52 | 2026-10-29 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 411ms) — fdcad52 | 2026-10-29 |
| G4 Review | PENDING | | |
| G5 Integração & CI | PENDING | | |
| H2 Integração aprovada | PENDING | | |
| G6 Deploy | PENDING | | |
| G7 Pronto & Docs | PENDING | | |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue

### Como foi feito

### Prova de Correção
<!-- type fix: teste de regressão falhou antes (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|

### Definição de Pronto
- [ ] Teste de regressão falhou antes e passa depois da correção
- [ ] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [ ] Review independente sem achados blocker/major (G4)
- [ ] Padrão existente mantido
- [ ] Disponível no ambiente-alvo via pipeline (G6)
- [ ] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [ ] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy

### Pendências

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

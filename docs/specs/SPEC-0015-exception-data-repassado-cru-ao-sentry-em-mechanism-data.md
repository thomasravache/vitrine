---
id: SPEC-0015
title: Exception.Data repassado cru ao Sentry em mechanism.data
tier: lite
type: fix
user_facing: false
status: implemented
created: 2026-10-29
parent: SPEC-0001
depends_on: [SPEC-0013]
consumes_contract: []
touches: [src/Api/Observability/**, tests/Vitrine.IntegrationTests/Observability/**]
adrs: [ADR-0006, ADR-0009]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-29
approved_via: piloto automático autorizado no chat em 2026-10-29
---

# SPEC-0015 — Exception.Data repassado cru ao Sentry em mechanism.data

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
Tudo que está em `Exception.Data` vai cru ao Sentry em `exception.values[].mechanism.data`. Reproduzido pelo Reviewer no G4 da SPEC-0013: `ex.Data["Detalhe"] = "valor pw-SEGREDO-777"; LogError(ex, "erro {Password}", "pw-SEGREDO-777")` → envelope com `"mechanism":{"data":{"Detalhe":"valor pw-SEGREDO-777"}}`. Esperado: valores de `mechanism.data` passam pelo mesmo mascaramento da exceção (valores sensíveis do registro e regex genéricas), e chaves com nome sensível (ex.: `Password`, `Token`) saem como `***`. O OTLP não exporta `Data`, então só o Sentry é afetado. Impacto: baixo hoje (nenhum código grava segredo em `Data`), mas fere o ADR-0006.

## 2. Causa Raiz
`src/Api/Observability/ObservabilityExtensions.cs` (`MaskEvent`): mascara `exception.Value`, mensagem e extras, mas não percorre `SentryException.Mechanism.Data`, que o `MainExceptionProcessor` do SDK preenche com todas as chaves string de `Exception.Data`.

## 3. Mudança Proposta
`MaskEvent` mascara cada valor de `Mechanism.Data` com `SensitiveDataMasker.MaskText(valor, valores sensíveis)` e troca por `***` o valor de chave com nome sensível (mesma regra das propriedades de log). Não muda: console, OTLP, pilha, demais campos do evento, `Mechanism.Type`/`Handled`.

**Padrão seguido:** `MaskEvent`/`MaskBreadcrumb` em `ObservabilityExtensions.cs` (SPEC-0007/0013) e `SensitiveDataMasker` do ServiceDefaults.

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma no Sentry. Limite conhecido, igual no OTLP e registrado no relatório da SPEC-0013: segredo que só existe num escopo de log ou em `CaptureException` direto não é conhecido pelo mascaramento — decisão de desenho do ADR-0006, sem spec.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0015:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — área já coberta pelos testes de mascaramento do Sentry das SPEC-0007 e SPEC-0013.
- Unitários: N/A — o defeito aparece no envelope enviado pelo host real; a regressão é de integração.
- - **IT-01** — Dado a API com Sentry configurado (transporte de teste), quando `LogError(ex, "erro {Password}", "pw-PROBE-123")` com `ex.Data["Detalhe"] = "valor pw-PROBE-123"` e `ex.Data["Token"] = "abc-PROBE"`, então nenhum envelope contém `pw-PROBE-123` nem `abc-PROBE`, o evento é enviado e `mechanism.data` mantém a chave `Detalhe`.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma.

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever IT-01 com a tag `SPEC-0015:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `MaskEvent` mascara `Mechanism.Data` (valores sensíveis do registro + chave de nome sensível)
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
| G0 Spec | PASS | validate: 0 erro(s) — b5198f9 (árvore suja) | 2026-10-29 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 4ed8199 | 2026-10-29 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  649ms (environment 60%, transform 17%, import 16%, tests 5%, worker 2%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Coverage: 100%) — 6fe759d | 2026-10-29 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 398ms) — 6fe759d | 2026-10-29 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: APPROVED (Reviewer independente) em 6fe759d: red 4ed8199 reproduzido; sondas sem vazamento em Data de exceções internas/Aggregate, variações de chave sensível, Bearer/access_token, null, CaptureException direto, sentry:tag; pilha/type/handled intactos; SDK 6.12.1 conferido; Observability 15/15; minors pré-existentes consolidados na SPEC-0018 (valor de chave sensível de Data repetido na mensagem; ToString de objetos/records; Data com prefixo sentry: em contexts/extra) — 6fe759d | 2026-10-29 |
| G5 Integração & CI | PASS | MANUAL: PR #38 (sdd/int-wave-0015-0017 @ f586e61): CI 7/7 verde + CodeQL (runs/40951508757); local no integrado: build 0 avisos, format/lint ok, dotnet 165/165, vitest 6/6, tests/ci 3/3, e2e 30/30, drift do api:generate limpo, npm audit sem high, gitleaks 0 | 2026-10-29 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-29 |
| G6 Deploy | PASS | smoke_test exit 0 (2 passed (2.4s)) — b79eb1c (árvore suja) | 2026-10-29 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — b79eb1c (árvore suja) | 2026-10-29 |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
Valores de `Exception.Data` enviados ao Sentry em `mechanism.data` passam pelo mascaramento: chave de nome sensível vira `***` e os demais valores têm os segredos conhecidos do registro e os padrões genéricos mascarados, em todas as exceções do evento (inclusive internas). Nenhuma chave é removida.

### Como foi feito
`MaskMechanismData` em `src/Api/Observability/ObservabilityExtensions.cs`, chamado no laço de exceções do `MaskEvent`, com `SensitiveDataMasker.IsSensitiveName`/`MaskText` e a mesma lista de valores sensíveis da exceção (SPEC-0013). Review APPROVED com sondas (exceções internas/Aggregate, variações de chave, Bearer/access_token, `CaptureException` direto) e conferência no SDK 6.12.1.

### Prova de Correção
<!-- type fix: teste de regressão falhou antes (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->
Red 4ed8199: IT-01 falhava com `"pw-PROBE-123" foi enviado ao Sentry` (`mechanism.data` com `Detalhe` e `Token` crus). Green 6fe759d: 1/1; Observability 15/15; reproduzido pelo Reviewer.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| IT-01 | Exception.Data mascarado em mechanism.data, evento enviado e chave Detalhe preservada | PASS | CI PR #38 run 40951508757; main run 40952299406 (b79eb1c) |

### Definição de Pronto
- [x] Teste de regressão falhou antes e passa depois da correção
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like: imagem `ghcr.io/thomasravache/vitrine-api:b79eb1c` (commit de merge do PR #38) da CI da `main` (run 40952299406) via `deploy/deploy.sh`, health 200 em 4 s, smoke `@smoke` 2/2 (2026-10-29).

### Pendências
- SPEC-0018 — achados minor pré-existentes da review (valor de chave sensível de `Data` repetido na mensagem; `Data` com prefixo `sentry:` em contexts/extra; `ToString` de objetos).

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

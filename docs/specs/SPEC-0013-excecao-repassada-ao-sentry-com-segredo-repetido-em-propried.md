---
id: SPEC-0013
title: Exceção repassada ao Sentry com segredo repetido em propriedade sensível
tier: lite
type: fix
user_facing: false
status: implemented
created: 2026-10-28
parent: SPEC-0001
depends_on: []
consumes_contract: []
touches: [src/Api/Observability/**, src/ServiceDefaults/Logging/**, tests/Vitrine.IntegrationTests/Observability/**]
adrs: [ADR-0006, ADR-0009]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-28
approved_via: piloto automático autorizado no chat em 2026-10-28
---

# SPEC-0013 — Exceção repassada ao Sentry com segredo repetido em propriedade sensível

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
Um `LogError(ex, "{Password}", valor)` cuja exceção também contém `valor` no texto envia ao Sentry a exceção com `valor` em claro, quando o valor não tem formato de e-mail, `Bearer`/`Basic` nem `nome=valor`. Reproduzido pelo Reviewer no G4 da SPEC-0007: `exception.value = "PROBEEXC falhou para *** pw-PROBE-123"`. Esperado: o valor sai mascarado (`***`), como já acontece no pipeline OTLP. Impacto: improvável (exige o mesmo segredo na exceção e numa propriedade sensível), mas fere o ADR-0006.

## 2. Causa Raiz
`src/Api/Observability/MaskingLoggerProvider.cs`: o decorador mascara propriedades e mensagem mas repassa a exceção original; no `MaskEvent` as tags já chegam como `***`, então a lista de valores sensíveis fica vazia e a exceção só passa pelas regex genéricas.

## 3. Mudança Proposta
Muda: o decorador passa ao Sentry os valores sensíveis do registro junto com a exceção (ex.: `MaskedException.MaskIfNeeded` quando há valores, ou marcação interna em `exception.Data` lida pelo `MaskEvent`). Não muda: logs OTLP, console JSON e SPA.

**Padrão seguido:** `SensitiveDataMaskingProcessor` + `MaskedException` (caminho OTLP, SPEC-0007 M1)

**Rollback:** revert do commit

**Outras ocorrências:** Nenhuma — o caminho OTLP já trata (coberto pelos testes de integração da SPEC-0007).

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0013:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — área coberta pelos testes de mascaramento da SPEC-0007.
- **UT-01** — Dado a API com Sentry configurado (transporte de teste), quando `LogError(new InvalidOperationException("falhou para pw-PROBE-123"), "{Password}", "pw-PROBE-123")`, então nenhum envelope enviado contém `pw-PROBE-123` (exceção, mensagem, tags) e o evento é enviado.
- Integração/E2E: N/A — o UT-01 já roda no host real da API (mesmo padrão dos testes de Sentry da SPEC-0007).

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever UT-01 com a tag `SPEC-0013:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `MaskingLoggerProvider` repassa ao Sentry a exceção com os valores das propriedades sensíveis mascarados
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
| G0 Spec | PASS | validate: 0 erro(s) — 58742a0 (árvore suja) | 2026-10-28 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 549eeaa | 2026-10-29 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  388ms (environment 58%, import 18%, transform 18%, tests 4%, worker 2%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Coverage: 100%) — c468645 | 2026-10-29 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 530ms) — c468645 | 2026-10-29 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: APPROVED (Reviewer independente) em c468645: red 549eeaa e green reproduzidos; 8 sondas sem vazamento (AggregateException, breadcrumb do evento seguinte, Warning, 64 logs paralelos, pilha preservada); ThreadStatic+finally conferido no SDK 6.12.1; Observability 14/14; minors pré-existentes viram pendência (Exception.Data em mechanism.data; segredo só em escopo/CaptureException direto) — c468645 | 2026-10-29 |
| G5 Integração & CI | PASS | MANUAL: PR #36 (sdd/int-wave-0011-0014 @ 361f23f): CI 7/7 verde + CodeQL (runs/40941406441); local no integrado: build 0 avisos, format/lint ok, dotnet 153/153, vitest 6/6, tests/ci 3/3, e2e 30/30, drift do api:generate limpo, npm audit sem high, gitleaks 0 | 2026-10-29 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-29 |
| G6 Deploy | PASS | smoke_test exit 0 (2 passed (2.3s)) — 96ce8e8 (árvore suja) | 2026-10-29 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 96ce8e8 (árvore suja) | 2026-10-29 |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
A exceção repassada ao Sentry sai com o valor das propriedades sensíveis do mesmo registro mascarado (`***`) — também nos breadcrumbs `exception_message` dos eventos seguintes —, como já acontecia no OTLP. A pilha real é preservada.

### Como foi feito
`MaskingLoggerProvider` publica, durante o `inner.Log`, os valores originais das propriedades sensíveis num campo `[ThreadStatic]` (restaurado no `finally`); `MaskEvent`/`MaskBreadcrumb` em `ObservabilityExtensions.cs` usam esses valores com `SensitiveDataMasker.MaskText`. Funciona porque o SDK do Sentry 6.12.1 chama `BeforeSend`/`BeforeBreadcrumb` de forma síncrona dentro do `Log` (conferido no código do SDK pelo Reviewer). Descartadas: `MaskedException` (perde a pilha) e `Exception.Data` (o Sentry envia `Data`). Review APPROVED com 8 sondas sem vazamento.

### Prova de Correção
<!-- type fix: teste de regressão falhou antes (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->
Red 549eeaa: UT-01 falhava com `"pw-PROBE-123" foi enviado ao Sentry` (`exception.value = "falhou para pw-PROBE-123"`, tag `Password` já `***`). Green c468645: UT-01 passa (1/1), Observability 14/14; reproduzido pelo Reviewer nos dois commits.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| UT-01 | Exceção com o segredo de propriedade sensível sai mascarada no Sentry e o evento é enviado | PASS | CI PR #36 run 40941406441 (361f23f); main run 40942228466 (96ce8e8) |

### Definição de Pronto
- [x] Teste de regressão falhou antes e passa depois da correção
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like: imagem `ghcr.io/thomasravache/vitrine-api:96ce8e8` (commit de merge do PR #36) da CI da `main` (run 40942228466) via `deploy/deploy.sh`, health 200 em 3 s, smoke `@smoke` 2/2 (2026-10-29).

### Pendências
- SPEC-0015 — `Exception.Data` vai cru ao Sentry em `mechanism.data` (minor pré-existente achado na review).
- Limite conhecido, igual ao OTLP (ADR-0006, sem spec): segredo que só existe num escopo de log ou em `CaptureException` direto não é conhecido pelo mascaramento.
- Nit aceito: o UT-01 não trava o breadcrumb do evento seguinte (coberto pelas sondas da review).

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

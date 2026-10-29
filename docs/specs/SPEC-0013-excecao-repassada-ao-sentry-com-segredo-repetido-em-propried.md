---
id: SPEC-0013
title: Exceção repassada ao Sentry com segredo repetido em propriedade sensível
tier: lite
type: fix
user_facing: false
status: in-progress
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
- [ ] Red: escrever UT-01 com a tag `SPEC-0013:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `MaskingLoggerProvider` repassa ao Sentry a exceção com os valores das propriedades sensíveis mascarados
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
| G0 Spec | PASS | validate: 0 erro(s) — 58742a0 (árvore suja) | 2026-10-28 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 549eeaa | 2026-10-29 |
| G2 Green | PENDING | | |
| G3 Arquitetura | PENDING | | |
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

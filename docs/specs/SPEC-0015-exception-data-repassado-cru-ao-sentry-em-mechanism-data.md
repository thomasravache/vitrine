---
id: SPEC-0015
title: Exception.Data repassado cru ao Sentry em mechanism.data
tier: lite
type: fix
user_facing: false
status: approved
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
- [ ] Red: escrever IT-01 com a tag `SPEC-0015:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `MaskEvent` mascara `Mechanism.Data` (valores sensíveis do registro + chave de nome sensível)
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
| G0 Spec | PASS | validate: 0 erro(s) — b5198f9 (árvore suja) | 2026-10-29 |
| G1 Red | PENDING | | |
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

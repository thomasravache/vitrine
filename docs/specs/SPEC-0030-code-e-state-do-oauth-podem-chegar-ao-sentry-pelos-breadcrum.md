---
id: SPEC-0030
title: Code e state do OAuth podem chegar ao Sentry pelos breadcrumbs de navegação da SPA
tier: lite
type: fix
user_facing: false
status: in-progress
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0025]
consumes_contract: []
touches: [web/src/observability/**]
adrs: [ADR-0006, ADR-0009]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-30
approved_via: piloto automático autorizado no chat em 2026-10-28/30 (épico SPEC-0019)
---

# SPEC-0030 — Code e state do OAuth podem chegar ao Sentry pelos breadcrumbs de navegação da SPA

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
Na volta do OAuth (`/contas/mercado-livre/retorno?code=…&state=…`, SPEC-0025), o `history.replaceState` que limpa a URL gera um breadcrumb de navegação do Sentry cujo `from` contém `code` e `state`. A máscara da SPA (`web/src/observability/sentry.ts`) só cobre nomes sensíveis conhecidos; `code`/`state` passam. Com o DSN configurado, esses valores podem chegar ao Sentry. Risco baixo (o code é de uso único e consumido em segundos), mas viola a regra da ADR-0006 de não mandar credenciais a terceiros. Esperado: a query do retorno do OAuth nunca sai da SPA.

## 2. Causa Raiz
`web/src/observability/sentry.ts` (`maskText`/`beforeBreadcrumb`): a lista de parâmetros mascarados não inclui `code` e `state`, e URLs de navegação não têm a query mascarada.

## 3. Mudança Proposta
- O `beforeBreadcrumb`/`beforeSend` da SPA mascara `code` e `state` em qualquer URL e remove a query inteira de `/contas/mercado-livre/retorno`.
- **Não muda:** a tela de retorno (SPEC-0025) e a configuração do Sentry no backend.

**Padrão seguido:** Máscara existente em `web/src/observability/sentry.ts` (SPEC-0007/0013).

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma: o backend não loga a query do retorno (a SPA chama `complete` por POST).

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0030:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — os testes da máscara da SPEC-0007 seguem como guarda.
- **UT-01** — Dado um breadcrumb de navegação com `from=/contas/mercado-livre/retorno?code=TG-abc&state=xyz` e outro com `?code=1&state=2` em outra rota, quando passam pelo `beforeBreadcrumb`, então nenhum contém os valores de `code`/`state`.
- Integração/E2E: N/A — a máscara é função pura; o UT-01 reproduz o defeito.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever UT-01 com a tag `SPEC-0030:UT-01` e confirmar que falha pelo motivo certo
- [ ] Green: máscara de `code`/`state` e da query do retorno do OAuth
- [ ] Validar: build + suíte completa (G2/G3)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — cf475ad (árvore suja) | 2026-10-30 |
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

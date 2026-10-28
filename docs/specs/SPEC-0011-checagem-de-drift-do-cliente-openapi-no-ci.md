---
id: SPEC-0011
title: Checagem de drift do cliente OpenAPI no CI
tier: lite
type: feature
user_facing: false
status: approved
created: 2026-10-28
parent: SPEC-0001
depends_on: []
consumes_contract: []
touches: [.github/workflows/ci.yml, tests/ci/**]
adrs: [ADR-0013, ADR-0004]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-28
approved_via: piloto automático autorizado no chat em 2026-10-28
---

# SPEC-0011 — Checagem de drift do cliente OpenAPI no CI

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: feature | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
O cliente TypeScript da API (`web/src/api`, gerado pelo orval a partir do OpenAPI) é versionado, mas nada no CI verifica que ele corresponde à API atual. Uma mudança de endpoint sem rodar `npm run api:generate` passa no CI e a SPA chama um contrato velho. Esperado: o CI reprova quando o cliente gerado diverge.

## 2. Causa Raiz
Lacuna do pipeline (SPEC-0004/SPEC-0006): a geração existe (`api:generate` em `web/package.json`), mas não há passo de verificação no job `build-lint` de `.github/workflows/ci.yml`. Pendência levantada no G7 da SPEC-0006.

## 3. Mudança Proposta
Muda: passo no CI `npm --prefix web run api:generate && git diff --exit-code web/src/api` com mensagem clara de como corrigir. Não muda: o gerador, a API nem o cliente.

**Padrão seguido:** testes de pipeline em `tests/ci/` (SPEC-0004) e passos do job `build-lint`

**Rollback:** revert do commit

**Outras ocorrências:** Nenhuma

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0011:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — sem comportamento de produto alterado.
- **UT-01** — Dado o workflow de CI, quando lido pelo teste de pipeline, então o job `build-lint` tem o passo de verificação de drift do cliente OpenAPI depois do build.
- **IT-01** — Dado uma cópia do repositório com um endpoint alterado e o cliente não regenerado, quando o passo de verificação roda, então sai com código diferente de zero e aponta `npm run api:generate`.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 1f05dea | 2026-10-28 |
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

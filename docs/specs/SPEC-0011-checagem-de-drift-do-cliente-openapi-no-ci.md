---
id: SPEC-0011
title: Checagem de drift do cliente OpenAPI no CI
tier: lite
type: feature
user_facing: false
status: in-progress
created: 2026-10-28
parent: SPEC-0001
depends_on: []
consumes_contract: []
touches: [.github/workflows/ci.yml, tests/ci/**, src/Api/Program.cs, src/Api/Security/SecurityExtensions.cs, src/Api/OpenApiGeneration.cs, web/package.json, tests/Vitrine.IntegrationTests/OpenApi/**]
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
- **IT-01** — Dado uma cópia do repositório sem PostgreSQL e sem `DataProtection__MasterKey` no ambiente, quando o passo de verificação roda com o cliente em dia, então sai com 0; e, com um endpoint alterado e o cliente não regenerado, sai com código diferente de zero e aponta `npm run api:generate`.
- **IT-02** — Dado um host da API em `Production` sem `DataProtection__MasterKey` e sem PostgreSQL, quando os serviços são registrados no modo de geração do OpenAPI (o mesmo ponto que o `Program.cs` usa), então o host constrói e inicia, o Data Protection protege e desprotege em memória e nenhuma checagem do anel é registrada; no modo normal, sem chave, a subida falha com a mensagem da chave mestra.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever UT-01, IT-01 com a tag `SPEC-0011:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: passo de drift (`npm run api:generate` + `git diff --exit-code web/src/api`) no job `build-lint`
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
| G0 Spec | PASS | validate: 0 erro(s) — 1f05dea | 2026-10-28 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 2d667cb | 2026-10-29 |
| G2 Green | FAIL | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  619ms (environment 58%, transform 19%, import 17%, tests 4%, worker 3%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 1 (Failure. Coverage is below 80%.) — b062cb3 | 2026-10-29 |
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
| IMP-01 | 2026-10-29 | G1 | spec | npm run api:generate quebrado desde a SPEC-0008: em Production exige DataProtection__MasterKey e PostgreSQL (checagem do anel); em Development a validação de DI falha (IDbContextOutbox sem IWolverineRuntime, pois UseWolverine é pulado na geração). O passo de drift falharia mesmo com o cliente em dia. | Test-writer: protótipo com Postgres de serviço + chave falsa no passo passa, mas deixa api:generate quebrado para quem não tem banco | Architect | Emenda v1.1 — geração do OpenAPI sem banco/chave no escopo; IT-01 sem PostgreSQL | 2026-10-29 |

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
| v1.2 (escopo) | 2026-10-29 | IT-02 em processo para o modo de geração do OpenAPI; a configuração desse modo sai do `Program.cs` para `src/Api/OpenApiGeneration.cs` (testável); `touches` ganha esse arquivo e `tests/Vitrine.IntegrationTests/OpenApi/**` | G2 FAIL: diff-cover 42% — o ramo da geração só roda fora do processo (IT-01 em shell), invisível à cobertura | nenhuma | thomas (2026-10-29, piloto automático) |
| v1.1 (escopo) | 2026-10-29 | `npm run api:generate` volta a funcionar sem banco e sem chave mestra (a geração do OpenAPI não valida DI do outbox nem exige/checa o anel do Data Protection); `touches` ganha `src/Api/Program.cs`, `src/Api/Security/SecurityExtensions.cs` e `web/package.json`; IT-01 roda sem PostgreSQL e sem chave | IMP-01: a geração quebrou na SPEC-0008 e o passo de drift falharia mesmo com o cliente em dia | nenhuma | thomas (2026-10-29, piloto automático) |

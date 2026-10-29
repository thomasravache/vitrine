---
id: SPEC-0011
title: Checagem de drift do cliente OpenAPI no CI
tier: lite
type: feature
user_facing: false
status: implemented
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
- [x] Red: escrever UT-01, IT-01 com a tag `SPEC-0011:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: passo de drift (`npm run api:generate` + `git diff --exit-code web/src/api`) no job `build-lint`
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
| G0 Spec | PASS | validate: 0 erro(s) — 1f05dea | 2026-10-28 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 2d667cb | 2026-10-29 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  360ms (environment 63%, transform 16%, import 15%, tests 5%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Coverage: 100%) — 1442459 | 2026-10-29 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 473ms) — 1442459 | 2026-10-29 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: APPROVED (Reviewer independente) em 1442459: modo de geração inalcançável no runtime real (publish sem GetDocument; binário publicado sem chave em Production → exit 134 com a mensagem da chave); drift falha com orval quebrado e com arquivo não rastreado; api:generate sem banco/chave com cliente idêntico; red reproduzido (sem passo; passo sem correção falha no controle; IT-02 não compila); tests/ci 3/3, IT-01 1/1, IT-02 4/4, integração 91/91; nits: bash -e vs pipefail no IT-01, tipo fix(api) — 1442459 | 2026-10-29 |
| G5 Integração & CI | PASS | MANUAL: PR #36 (sdd/int-wave-0011-0014 @ 361f23f): CI 7/7 verde + CodeQL (runs/40941406441); local no integrado: build 0 avisos, format/lint ok, dotnet 153/153, vitest 6/6, tests/ci 3/3, e2e 30/30, drift do api:generate limpo, npm audit sem high, gitleaks 0 | 2026-10-29 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-29 |
| G6 Deploy | PASS | smoke_test exit 0 (2 passed (3.2s)) — 96ce8e8 (árvore suja) | 2026-10-29 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 96ce8e8 (árvore suja) | 2026-10-29 |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-29 | G1 | spec | npm run api:generate quebrado desde a SPEC-0008: em Production exige DataProtection__MasterKey e PostgreSQL (checagem do anel); em Development a validação de DI falha (IDbContextOutbox sem IWolverineRuntime, pois UseWolverine é pulado na geração). O passo de drift falharia mesmo com o cliente em dia. | Test-writer: protótipo com Postgres de serviço + chave falsa no passo passa, mas deixa api:generate quebrado para quem não tem banco | Architect | Emenda v1.1 — geração do OpenAPI sem banco/chave no escopo; IT-01 sem PostgreSQL | 2026-10-29 |

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
O CI reprova o PR quando o cliente TypeScript gerado (`web/src/api`) não corresponde ao OpenAPI da API: passo "OpenAPI client drift" no job `build-lint`, com `::error` que manda rodar `npm run api:generate`. De quebra, `npm run api:generate` voltou a funcionar sem PostgreSQL e sem chave mestra (estava quebrado desde a SPEC-0008).

### Como foi feito
`src/Api/OpenApiGeneration.cs` concentra o modo de geração (detecção pelo assembly de entrada `GetDocument.Insider`; `ValidateOnBuild=false`; `AddSecurity(generatingOpenApi: true)` usa Data Protection efêmero, sem chave mestra nem checagem do anel). Passo no `ci.yml` depois do Build: `api:generate` + `git status --porcelain -- web/src/api` (pega também arquivos novos). Testes em `tests/ci/` (UT-01 lê o workflow; IT-01 executa o passo como escrito numa cópia do repo sem banco/chave) e `tests/Vitrine.IntegrationTests/OpenApi/` (IT-02). Emendas v1.1 (IMP-01: geração quebrada) e v1.2 (G2 FAIL por cobertura: ramo só rodava fora do processo). Review APPROVED: modo de geração inalcançável no runtime real (publish sem GetDocument; binário sem chave em Production não sobe).

### Prova de Correção
<!-- type fix: teste de regressão falhou antes (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->
N/A — type feature.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| UT-01 | build-lint tem o passo de drift depois do build | PASS | CI PR #36 run 40941406441 (361f23f); main run 40942228466 (96ce8e8) |
| IT-01 | Sem banco/chave: cliente em dia sai 0; endpoint alterado sai ≠0 com a dica `npm run api:generate` | PASS | CI PR #36 run 40941406441 (361f23f); main run 40942228466 (96ce8e8) |
| IT-02 | Modo de geração sobe sem chave/banco com Data Protection em memória; modo normal sem chave não sobe | PASS | CI PR #36 run 40941406441 (361f23f); main run 40942228466 (96ce8e8) |

### Definição de Pronto
- [x] Testes do plano falharam antes (sem passo; geração quebrada; IT-02 sem `OpenApiGeneration`) e passam depois
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like: imagem `ghcr.io/thomasravache/vitrine-api:96ce8e8` (commit de merge do PR #36) da CI da `main` (run 40942228466) via `deploy/deploy.sh`, health 200 em 3 s, smoke `@smoke` 2/2 (2026-10-29).

### Pendências
- SPEC-0012 (já aprovada) — testes de infra deixam a stack local rodando.
- Nits da review aceitos sem spec: IT-01 roda o passo com `bash -eo pipefail` e o Actions usa `bash -e` (sem pipe no script, sem efeito); regeneração incremental local do `openapi.json` depende do cache em `obj/` (comportamento do pacote, CI não afetado).

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.2 (escopo) | 2026-10-29 | IT-02 em processo para o modo de geração do OpenAPI; a configuração desse modo sai do `Program.cs` para `src/Api/OpenApiGeneration.cs` (testável); `touches` ganha esse arquivo e `tests/Vitrine.IntegrationTests/OpenApi/**` | G2 FAIL: diff-cover 42% — o ramo da geração só roda fora do processo (IT-01 em shell), invisível à cobertura | nenhuma | thomas (2026-10-29, piloto automático) |
| v1.1 (escopo) | 2026-10-29 | `npm run api:generate` volta a funcionar sem banco e sem chave mestra (a geração do OpenAPI não valida DI do outbox nem exige/checa o anel do Data Protection); `touches` ganha `src/Api/Program.cs`, `src/Api/Security/SecurityExtensions.cs` e `web/package.json`; IT-01 roda sem PostgreSQL e sem chave | IMP-01: a geração quebrou na SPEC-0008 e o passo de drift falharia mesmo com o cliente em dia | nenhuma | thomas (2026-10-29, piloto automático) |

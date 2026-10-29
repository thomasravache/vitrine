---
id: SPEC-0012
title: Testes de infra deixam a stack local rodando
tier: lite
type: fix
user_facing: false
status: implemented
created: 2026-10-28
parent: SPEC-0001
depends_on: []
consumes_contract: []
touches: [tests/infra/**]
adrs: [ADR-0004]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-28
approved_via: piloto automático autorizado no chat em 2026-10-28
---

# SPEC-0012 — Testes de infra deixam a stack local rodando

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
`bash tests/infra/run.sh` termina com o ambiente local (projeto Compose `vitrine`: caddy, api, postgres) ainda no ar, ocupando as portas 80/443 e o volume do banco. Esperado: o runner devolve a máquina como encontrou (derruba o que subiu), salvo pedido explícito para manter.

## 2. Causa Raiz
`tests/infra/run.sh` não tem limpeza ao sair; só IT-07 tem `trap cleanup EXIT` próprio, os demais IT sobem a stack via `deploy.sh` e não a derrubam. Pendência herdada do G7 da SPEC-0005.

## 3. Mudança Proposta
Muda: `run.sh` registra `trap` que roda `docker compose -p vitrine down -v` quando ele próprio subiu a stack (não derruba uma stack que já estava no ar antes), com `--keep` para depuração. Não muda: os testes IT-01..IT-07 nem o `deploy.sh`.

**Padrão seguido:** `trap cleanup EXIT` de `tests/infra/IT-07.sh` e helpers de `tests/infra/lib.sh`

**Rollback:** revert do commit

**Outras ocorrências:** Nenhuma

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0012:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — sem comportamento de produto alterado.
- **UT-01** — Dado nenhuma stack `vitrine` no ar, quando `run.sh` executa um IT que sobe a stack e termina, então `docker compose -p vitrine ps -q` fica vazio; com `--keep`, os containers continuam.
- **IT-01** — Dado a stack `vitrine` já no ar antes do runner, quando `run.sh` termina, então ela continua no ar (o runner só derruba o que subiu).

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever UT-01, IT-01 com a tag `SPEC-0012:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `tests/infra/run.sh` derruba só a stack que subiu (`--keep` preserva)
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
| G0 Spec | PASS | validate: 0 erro(s) — 1f05dea (árvore suja) | 2026-10-28 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — 12480c4 | 2026-10-29 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  351ms (environment 62%, transform 16%, import 16%, tests 4%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (No lines with coverage information in this diff.) — dc89fcd | 2026-10-29 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 094ms) — dc89fcd | 2026-10-29 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: APPROVED (Reviewer independente, 1ª rodada + delta) em dc89fcd: UT-01 red em 12480c4 e 22ddb3e pelo motivo certo, 4/4 no green; IT-01 guarda nos dois ramos; suíte de infra 10/10 derruba só o que subiu; sondas: falha/sucesso preservam rc, SIGINT/SIGTERM limpam, stack no ar e Docker indisponível intocados, outro projeto Compose intacto, projeto parado mantém containers e volumes; shellcheck limpo; nits: rede vitrine_default sobra no modo stopped, --force-recreate remove o container parado original, SIGTERM espera o teste em andamento — dc89fcd | 2026-10-29 |
| G5 Integração & CI | PASS | MANUAL: PR #40 (sdd/int-wave-0012-0018 @ d0dbdfb): CI 7/7 verde + CodeQL (runs/40962468558); local no integrado: build 0 avisos, format/lint ok, dotnet 190/190, vitest 6/6, tests/ci 3/3, e2e 30/30, drift do api:generate limpo, npm audit sem high, gitleaks 0 | 2026-10-29 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-29 |
| G6 Deploy | PASS | smoke_test exit 0 (2 passed (3.3s)) — 26e550a (árvore suja) | 2026-10-29 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 26e550a (árvore suja) | 2026-10-29 |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
`bash tests/infra/run.sh` devolve a máquina como encontrou: derruba (com `down -v`) só o projeto Compose `vitrine` que ele mesmo criou; uma stack que já estava no ar fica intocada; um projeto que existia parado mantém containers e volumes (só os containers criados no run são removidos); com `--keep` (em qualquer posição) nada é derrubado. Também limpa em Ctrl-C/SIGTERM e preserva o código de saída.

### Como foi feito
`tests/infra/run.sh`: estado do projeto no início (`up`/`absent`/`stopped`/`unknown`) e `trap cleanup EXIT` (padrão do `IT-07.sh`) + traps INT/TERM; `tag()` como em `tests/ci/run.sh`. Testes em `tests/infra/SPEC-0012-*` com um IT de fixture leve (só o postgres do projeto). Review: APPROVED com 2 minors (projeto parado perdia o volume; `--keep` só antes dos IDs) corrigidos e reconferidos.

### Prova de Correção
<!-- type fix: teste de regressão falhou antes (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->
Red 12480c4/22ddb3e: UT-01 falhava com `run.sh SPEC-0012-FX-up left 1 container(s) of 'vitrine' running`, `--keep` tratado como ID e, no projeto parado, `removed volume(s) ... vitrine_postgres_data`. Green cf5fc1e/dc89fcd: UT-01 4/4; IT-01 (guarda: stack já no ar continua) passa antes e depois; suíte de infra 10/10 sem deixar containers.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| UT-01 | Sem stack antes: nada fica no ar; --keep (antes ou depois dos IDs) mantém; projeto parado mantém volumes | PASS | CI PR #40 run 40962468558; main run 40963136253 (26e550a) |
| IT-01 | Stack já no ar antes do runner continua no ar depois | PASS | CI PR #40 run 40962468558; main run 40963136253 (26e550a) |

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
- Nits aceitos sem spec: a rede `vitrine_default` sobra no modo parado; teste que recria containers (`--force-recreate`) remove o container parado original (volumes preservados); SIGTERM só no runner espera o teste em andamento antes da limpeza.
- Achado lateral das reviews: `Vitrine.Tooling.Tests.LintTests` falhou 2x de forma intermitente em clones sem `npm ci` prévio (corrida com o `npm install` do AppHost); não aparece no CI nem no checkout principal — registrado aqui, sem spec por enquanto.

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

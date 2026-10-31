---
id: SPEC-0031
title: Simulador do ML repete IDs de vendedor depois de reiniciar
tier: lite
type: fix
user_facing: false
status: implemented
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0024]
consumes_contract: []
touches: [tools/MercadoLivreSimulator/**, tests/Vitrine.UnitTests/MercadoLivreSimulator/**, tests/Vitrine.IntegrationTests/MercadoLivreSimulator/**]
adrs: [ADR-0016]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-30
approved_via: piloto automático autorizado no chat em 2026-10-28/30 (épico SPEC-0019)
---

# SPEC-0031 — Simulador do ML repete IDs de vendedor depois de reiniciar

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
O simulador do ML (SPEC-0024) numera os vendedores criados por `POST /__sim/sellers` a partir de `3000001000`, em memória, e recomeça a cada reinício do container. O banco do Vitrine persiste entre deploys e guarda as contas conectadas por execuções anteriores do E2E (`ml_user_id` único). Depois de um reinício, o simulador devolve IDs já usados, e a conexão responde 409 `ML_ACCOUNT_IN_USE` ("já está conectada em outro workspace"). Reprodução: deploy local, rodar o smoke (`conectar-conta`), reimplantar (o simulador reinicia), rodar o smoke de novo → E2E-01 falha. Impacto: smoke do G6 instável no ambiente local; teste manual do usuário pode bater no mesmo erro. Esperado: IDs de vendedores criados nunca se repetem entre reinícios.

## 2. Causa Raiz
`tools/MercadoLivreSimulator/SimulatorState.cs`: o próximo ID de vendedor é um contador em memória com valor inicial fixo; não depende de nada que mude entre execuções.

## 3. Mudança Proposta
- Os IDs de `POST /__sim/sellers` passam a partir de uma base única por execução do processo (ex.: derivada do instante de início em milissegundos), sempre ≥ 3000001000, maiores que Int32 e sem repetir entre reinícios; dentro da execução continuam sequenciais.
- **Não muda:** os vendedores da massa inicial (`VENDEDOR_TESTE` 3000000001, `VENDEDOR_AR` 3000000002) e o contrato `/__sim/*`.

**Padrão seguido:** `SimulatorState` e `TimeProvider` do simulador (SPEC-0024).

**Rollback:** revert do commit.

**Outras ocorrências:** IDs de anúncios também recomeçam no reinício, mas não colidem no Vitrine (chave `(account_id, item_id)` e contas novas por execução) e a massa inicial depende da base fixa — sem mudança.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0031:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — os testes do simulador da SPEC-0024 seguem como guarda.
- Unidade: N/A — `SimulatorState` é `internal` e o defeito aparece no reinício do host; o IT-01 o reproduz no nível certo (Emenda v1.1).
- **IT-01** — Dado dois hosts do simulador iniciados um após o outro (`WebApplicationFactory`), quando cada um recebe `POST /__sim/sellers`, então os IDs devolvidos não se repetem.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever IT-01 com a tag `SPEC-0031:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: base de IDs única por execução do simulador
- [x] Validar: build + suíte completa (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like com smoke (G6)
- [x] Relatório de Entrega e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 76998b1 (árvore suja) | 2026-10-30 |
| G1 Red | PASS | MANUAL: verify G1 + red: IT-01 (56dfb9a) falha no HEAD com 'IDs repetidos depois do reinício: [3000001000, 3000001001, 3000001002]'; o comando de red do gate (unitários) não cobre IT | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (at least ~671ms faster with isolate: false — reuses workers across files instead of one pe); lint exit 0 ([0m[34mChecked 67 files in 46[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 100%) — 713ef80 | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 809ms) — 713ef80 | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED_WITH_NOTES (0 blocker/major; IDs < 2^53; reset sem voltar o contador sem teste dedicado — registrado) — 713ef80 | 2026-10-30 |
| G5 Integração & CI | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (learn more: https://vitest.dev/guide/improving-performance#test-environments); test_integration exit 0 (duration: 3m 42s 743ms); test_e2e exit 0 (40 passed (1.2m)); arch_test exit 0 (duration: 1s 575ms); security_scan exit 0 ([90m9:40PM[0m [32mINF[0m [1mno leaks found[0m); nota: PR #57 CI 8/8 verde + CodeQL (runs/41150155220) — ac99283 | 2026-10-30 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-30 |
| G6 Deploy | PASS | MANUAL: Deploy local prod-like (deploy/deploy.sh 0b9affd + simulador): health 200 em 4 s; smoke do sdd-config 4/4 (acesso + conectar-conta, desktop e mobile), reinício do simulador, smoke de novo 4/4; 0 logs Error/Critical; staging/produção na SPEC-0009 | 2026-10-30 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — cf8bb5b (árvore suja) | 2026-10-30 |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
Os vendedores criados no simulador do ML por `POST /__sim/sellers` não repetem mais IDs depois de reiniciar o simulador, então o smoke e o teste manual no ambiente local não colidem com contas que ficaram no banco do Vitrine (409 `ML_ACCOUNT_IN_USE`).

### Como foi feito
- `tools/MercadoLivreSimulator/SimulatorState.cs`: base dos IDs = `3000001000 + ms desde 2026-01-01 UTC`, lida do `TimeProvider` do DI na criação do estado; crescentes dentro da execução; bem abaixo de 2^53. `/__sim/reset` não volta o contador (Emenda v1.2). Massa inicial e IDs de anúncios inalterados.
- Emenda v1.1: só IT-01 (o estado é `internal`). Nota do G4: o comportamento do reset não tem teste dedicado.

### Prova de Correção
- Red: IT-01 (`56dfb9a`) — `IDs repetidos depois do reinício: [3000001000, 3000001001, 3000001002]`.
- Green: fix `713ef80`; 550/550 na suíte .NET; CI do PR #57 (runs/41150155220) verde; no ambiente local, smoke 4/4, reinício do simulador e smoke 4/4 de novo (antes falhava com "já está conectada em outro workspace").

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| IT-01 | Dois hosts do simulador em sequência não repetem IDs de vendedor | PASS | CI PR #57 runs/41150155220 (713ef80); main 0b9affd |

### Definição de Pronto
- [x] Teste de regressão falhou antes e passa depois da correção
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, `ghcr.io/thomasravache/vitrine-api:0b9affd` + `vitrine-ml-simulator:local`, 2026-10-30 (smoke 4/4 antes e depois de reiniciar o simulador). O simulador nunca vai a staging/produção.

### Pendências
Nenhuma.

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 | 2026-10-30 | Plano de testes só com IT-01 (UT sem acesso a `SimulatorState`, que é `internal`); IDs de anúncios ficam como estão (sem colisão no Vitrine; a massa inicial depende da base fixa) | BLOCKED do Test-writer no G1 | nenhuma | thomas (piloto automático do épico SPEC-0019) |
| v1.2 | 2026-10-30 | `/__sim/reset` deixa de voltar o contador de vendedores à base (um reset no meio da execução também não repete IDs); a massa inicial e os anúncios continuam voltando ao estado inicial | Decisão do Implementer aceita no G4 | nenhuma | thomas (piloto automático do épico SPEC-0019) |

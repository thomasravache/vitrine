---
id: SPEC-0014
title: Limitador sem partição por e-mail nulo e revogações no check do anel
tier: lite
type: fix
user_facing: false
status: in-progress
created: 2026-10-29
parent: SPEC-0001
depends_on: [SPEC-0008]
consumes_contract: []
touches: [src/Api/Security/AuthRateLimiting.cs, src/Api/Security/DataProtectionKeyRingCheck.cs, tests/Vitrine.IntegrationTests/Security/**]
adrs: [ADR-0006, ADR-0007]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-29
approved_via: piloto automático autorizado no chat em 2026-10-29
---

# SPEC-0014 — Limitador sem partição por e-mail nulo e revogações no check do anel

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
Dois nits aceitos no G4 da SPEC-0008 (re-review em 1458fee):

1. **Login sem e-mail:** `POST /api/v1/auth/login` com `email` nulo, vazio, só espaços, não-string ou ausente responde 401 após passar pelo Identity, e nunca cai no limite por e-mail (não há partição). Não é contorno — não há conta-alvo e o limite por IP vale —, mas cada tentativa custa uma ida ao Identity. Esperado: 400 `VALIDATION_FAILED` com erro no campo `email`, sem chegar ao Identity (mesmo formato do contrato SPEC-0006@1).
2. **Revogações no anel:** se um dia `IKeyManager.RevokeKey`/`RevokeAllKeys` for usado, o anel ganha elementos `<revocation>` sem `encryptedSecret`, e o `DataProtectionKeyRingCheck` recusa a subida fora de Development como se fosse chave em claro. Esperado: o check considera só elementos `<key>`; `<revocation>` é ignorado. Hoje nenhum código nem runbook revoga chaves — impacto futuro.

## 2. Causa Raiz
1. `src/Api/Security/AuthRateLimiting.cs` (`UseLoginEmailPartition`): só define a partição quando `Normalize` devolve e-mail; com zero e-mails ou e-mail inválido segue para o endpoint sem resposta própria.
2. `src/Api/Security/DataProtectionKeyRingCheck.cs`: exige `encryptedSecret` em todo elemento XML do anel, sem filtrar pelo nome `key`.

## 3. Mudança Proposta
No login, o middleware do limitador responde 400 `VALIDATION_FAILED` (`errors.email`) quando não há exatamente um `email` string não vazio; o check do anel filtra elementos `<key>` antes de exigir `encryptedSecret`. Não muda: cadastro (já validado pelo endpoint), limites, ordem 423 → 429, mensagens de erro da chave mestra, contrato SPEC-0006@1 (400 `VALIDATION_FAILED` já faz parte dele).

**Padrão seguido:** o próprio `AuthRateLimiting.cs` (resposta 400 de `email` duplicado via `WriteProblemAsync`) e `DataProtectionKeyRingCheck.cs`; testes em `AuthRateLimitBypassTests.cs` e `MasterKeyTests.cs`.

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0014:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — área já coberta pelos IT-03 e IT-08 da SPEC-0008.
- Unitários: N/A — os dois defeitos aparecem na fronteira HTTP e no banco; a regressão é de integração.
- - **IT-01** — Dado a API, quando `POST /api/v1/auth/login` chega com `email` nulo, `""`, `"   "`, `123` ou sem a propriedade, então responde 400 `VALIDATION_FAILED` com erro em `email`, e um login válido continua 401/204 como antes.
- **IT-02** — Dado um anel de chaves cifrado pela chave mestra com um elemento `<revocation>` gravado no PostgreSQL, quando o host sobe fora de Development, então sobe normalmente; e uma `<key>` em claro continua impedindo a subida.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma.

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever IT-01, IT-02 com a tag `SPEC-0014:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: 400 `VALIDATION_FAILED` no login sem e-mail válido; check do anel considera só `<key>`
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
| G0 Spec | PASS | validate: 0 erro(s) — 443e943 (árvore suja) | 2026-10-29 |
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

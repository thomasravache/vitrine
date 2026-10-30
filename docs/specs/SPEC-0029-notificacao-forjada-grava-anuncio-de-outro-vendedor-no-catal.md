---
id: SPEC-0029
title: Notificação forjada grava anúncio de outro vendedor no catálogo da conta
tier: lite
type: fix
user_facing: false
status: implemented
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0022]
consumes_contract: []
touches: [src/Modules/Listings/**, tests/Vitrine.IntegrationTests/Listings/**, tests/Vitrine.UnitTests/Listings/**]
adrs: [ADR-0006, ADR-0007]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-30
approved_via: piloto automático autorizado no chat em 2026-10-28/30 (épico SPEC-0019)
---

# SPEC-0029 — Notificação forjada grava anúncio de outro vendedor no catálogo da conta

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
O ML não assina as notificações. Um atacante que conheça o `application_id` do Vitrine (público, aparece na URL de autorização) e o `user_id` de um vendedor cliente (público no ML) envia ao webhook da SPEC-0023 `resource=/items/MLB<anúncio de outro vendedor>`. O processamento publica `MercadoLivreItemChanged` para a conta da vítima; o Listings relê o item com o token da vítima — o `/items/bulk` devolve itens públicos de qualquer vendedor — e grava o anúncio alheio no catálogo da vítima. Impacto: poluição do catálogo de outro tenant (até 600 itens/min por IP), reinjetável, persistente até a próxima sincronização completa. Não vaza dado privado. Esperado: anúncio de outro vendedor nunca entra no catálogo da conta.

## 2. Causa Raiz
`src/Modules/Listings/Vitrine.Listings.Infrastructure/Messaging/ItemChangedHandler.cs`: no caso `Found`, o único filtro é `item.Id == message.ItemId`; o `SellerId` do item (já mapeado pelo gateway) não é comparado com o `MlUserId` da conta. A sincronização completa não tem o problema, porque busca pelos IDs do próprio vendedor (`/users/{id}/items/search`).

## 3. Mudança Proposta
- No `ItemChangedHandler`, item `Found` com `SellerId` diferente do `MlUserId` da conta é ignorado: nada é gravado, a cópia local com esse `itemId` (se houver) é apagada, e a métrica `vitrine.listings.item_changes{result=foreign_item}` é incrementada.
- **Não muda:** o webhook (SPEC-0023), o gateway e a sincronização completa.

**Padrão seguido:** `ItemChangedHandler` e `ListingsUpsert` (SPEC-0022).

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma: a sincronização completa só lê IDs do próprio vendedor.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0029:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — os testes de notificação da SPEC-0022 seguem como guarda do caminho legítimo.
- Unidade: N/A — o defeito está no handler com banco; o IT-01 o reproduz.
- **IT-01** — Dado uma conta conectada com seus anúncios e o gateway devolvendo `Found` para um item cujo `SellerId` é de outro vendedor, quando chega `MercadoLivreItemChanged` desse item para a conta, então nenhuma linha é gravada para ele, a métrica `foreign_item` é incrementada e os anúncios da conta não mudam; e uma notificação de item do próprio vendedor continua atualizando normalmente.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever IT-01 com a tag `SPEC-0029:IT-01` e confirmar que falha pelo motivo certo
- [x] Green: `ItemChangedHandler` ignora item de outro vendedor e conta `foreign_item`
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like com smoke (G6)
- [x] Relatório de Entrega e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 570f56b (árvore suja) | 2026-10-30 |
| G1 Red | PASS | MANUAL: verify G1: PASS (IT-01 com tag no código, commit test 26f0ab2 antes de qualquer fix); red do IT-01 no HEAD 86c6509: Assert.DoesNotContain falhou — o anúncio de outro vendedor foi gravado no catálogo da conta; o comando de red do gate (testes unitários) não cobre IT | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  407ms (environment 62%, transform 16%, import 16%, tests 5%, worker 3%)); lint exit 0 ([0m[34mChecked 40 files in 17[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 97%) — 374860e | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 785ms) — 374860e | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED (2ª rodada; notificação forjada → SPEC-0029; retenção v1.2 pelo accepted_at; nit: comparar seller com o ml_user_id lido da conta) — 374860e | 2026-10-30 |
| G5 Integração & CI | PASS | MANUAL: Mesmo resultado integrado da SPEC-0023 (PR #51): build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  461ms (environment 61%, transform 20%, import 14%, tests 3%, worker 2%)); test_integration exit 0 (duration: 3m 44s 772ms); test_e2e exit 0 (30 passed (31.3s)); arch_test exit 0 (duration: 1s 788ms); security_scan exit 0 ([90m4:20PM[0m [32mINF[0m [1mno leaks found[0m); nota: PR #51 CI 8/8 verde + CodeQL (runs/41112611794) — 0051d64 | 2026-10-30 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-30 |
| G6 Deploy | PASS | MANUAL: Deploy local prod-like (deploy/deploy.sh 963c0fc --env-file deploy/local/.env): health 200 em 5 s; migração do inbox aplicada; smoke @smoke 2/2; POST /api/v1/mercadolivre/notifications sem credenciais do ML → 503 ML_NOT_CONFIGURED (esperado no local), GET → 404; 0 logs Error/Critical; staging/produção na SPEC-0009 | 2026-10-30 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 882c6c2 (árvore suja) | 2026-10-30 |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
Uma notificação (possivelmente forjada) de anúncio de outro vendedor não entra mais no catálogo da conta: o item é ignorado, uma cópia local com o mesmo ID é apagada e o caso é contado em `vitrine.listings.item_changes{result=foreign_item}`.

### Como foi feito
- `ItemChangedHandler`: no caso `Found`, compara `item.SellerId` com o `MlUserId` da conta antes do upsert.
- Apoio de teste: o dublê do gateway usa o vendedor da conta por padrão (`SetSeller`), e o teste de contrato do Listings declara o vendedor.

### Prova de Correção
- Red: IT-01 (`26f0ab2`) — o anúncio de outro vendedor era gravado no catálogo da conta (`Assert.DoesNotContain` falhou).
- Green: fix `325aa7f`; IT-01 passa; CI do PR #51 verde.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| IT-01 | Item de outro vendedor não é gravado, cópia alheia é apagada, métrica `foreign_item`; item próprio continua atualizando | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |

### Definição de Pronto
- [x] Teste de regressão falhou antes e passa depois da correção
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, imagem `ghcr.io/thomasravache/vitrine-api:963c0fc`, 2026-10-30, `deploy/deploy.sh` (health 200 em 5 s, smoke 2/2, 0 logs Error/Critical). Staging/produção na SPEC-0009.

### Pendências
Nenhuma.

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

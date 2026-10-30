---
id: SPEC-0029
title: Notificação forjada grava anúncio de outro vendedor no catálogo da conta
tier: lite
type: fix
user_facing: false
status: approved
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
- [ ] Red: escrever IT-01 com a tag `SPEC-0029:IT-01` e confirmar que falha pelo motivo certo
- [ ] Green: `ItemChangedHandler` ignora item de outro vendedor e conta `foreign_item`
- [ ] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 570f56b (árvore suja) | 2026-10-30 |
| G1 Red | FAIL | verify G1: FAIL — testes do plano sem tag no código versionado: SPEC-0029:UT-01, SPEC-00 — 86c6509 | 2026-10-30 |
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

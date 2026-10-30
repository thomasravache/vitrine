---
id: SPEC-0028
title: Handlers do Listings não são gerados pelo Wolverine com o gateway real do ML
tier: lite
type: fix
user_facing: false
status: approved
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0022]
consumes_contract: []
touches: [src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/MercadoLivreModule.cs, tests/Vitrine.IntegrationTests/Listings/**]
adrs: [ADR-0014]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-30
approved_via: piloto automático autorizado no chat em 2026-10-28/30 (épico SPEC-0019)
---

# SPEC-0028 — Handlers do Listings não são gerados pelo Wolverine com o gateway real do ML

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
Na Api real, os handlers do Wolverine que recebem `IMercadoLivreItems` (o `SyncListingsHandler` e o `ItemChangedHandler` do módulo Listings, SPEC-0022) não são gerados: o Wolverine recusa a geração de código porque a implementação `MercadoLivreItemsGateway` é `internal` (`ServiceLocationPolicy.NotAllowed`). Efeito: importação, sincronização manual, reconciliação e atualização por notificação nunca rodam com o gateway real — só nos testes, que trocam o gateway por um dublê público. Apareceu nos testes de integração da SPEC-0023, que publicam `MercadoLivreItemChanged` com o gateway real. Esperado: os handlers rodam com o gateway real.

## 2. Causa Raiz
`src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/MercadoLivreModule.cs`: o gateway é registrado como serviço scoped com implementação `internal` (G4 da SPEC-0021 tornou os tipos internos). A geração de código do Wolverine precisa construir o serviço diretamente ou ter permissão de *service location* para ele; sem nenhum dos dois, o handler não é gerado. Os testes do Listings registram um dublê singleton público, por isso não pegaram.

## 3. Mudança Proposta
- O módulo MercadoLivre declara `options.CodeGeneration.AlwaysUseServiceLocationFor<IMercadoLivreItems>()` ao configurar o Wolverine, permitindo que qualquer handler receba o gateway.
- **Não muda:** a visibilidade dos tipos do gateway, o contrato `IMercadoLivreItems` e os handlers do Listings.

**Padrão seguido:** Configuração do Wolverine em `MercadoLivreModule`/`ListingsModule` (ADR-0014).

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma: os demais handlers recebem só tipos de seus próprios módulos.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0028:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — os testes do Listings (SPEC-0022) seguem como guarda com o dublê.
- **UT-01** — Unidade: N/A — o defeito só aparece na geração de código do Wolverine com o host real; o IT-01 o reproduz.
- **IT-01** — Dado o host com o gateway **real** (sem dublê de `IMercadoLivreItems`) e o ML simulado por WireMock com uma conta conectada e dois anúncios, quando `SyncListings` é processado e, depois, chega um `MercadoLivreItemChanged`, então os anúncios são gravados e atualizados em `listings.listings` (sem erro de geração de código do Wolverine).

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever IT-01 com a tag `SPEC-0028:IT-01` e confirmar que falha pelo motivo certo
- [ ] Green: `AlwaysUseServiceLocationFor<IMercadoLivreItems>()` na configuração do Wolverine do módulo MercadoLivre
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
| G0 Spec | PASS | validate: 0 erro(s) — 6024a62 (árvore suja) | 2026-10-30 |
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

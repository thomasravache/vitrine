---
id: SPEC-0028
title: Handlers do Listings não são gerados pelo Wolverine com o gateway real do ML
tier: lite
type: fix
user_facing: false
status: implemented
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0022]
consumes_contract: []
touches: [src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/MercadoLivreModule.cs, tests/Vitrine.IntegrationTests/Listings/**, tests/Vitrine.IntegrationTests/MercadoLivre/**]
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
- Unidade: N/A — o defeito só aparece na geração de código do Wolverine com o host real; o IT-01 o reproduz.
- **IT-01** — Dado o host com o gateway **real** (sem dublê de `IMercadoLivreItems`) e o ML simulado por WireMock com uma conta conectada e dois anúncios, quando `SyncListings` é processado e, depois, chega um `MercadoLivreItemChanged`, então os anúncios são gravados e atualizados em `listings.listings` (sem erro de geração de código do Wolverine).

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever IT-01 com a tag `SPEC-0028:IT-01` e confirmar que falha pelo motivo certo
- [x] Green: `AlwaysUseServiceLocationFor<IMercadoLivreItems>()` na configuração do Wolverine do módulo MercadoLivre
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
| G0 Spec | PASS | validate: 0 erro(s) — 6024a62 (árvore suja) | 2026-10-30 |
| G1 Red | PASS | MANUAL: verify G1: PASS (test 02b4272 antes do fix 33f1cbd; IT-01 com tag no código); red do IT-01 num clone novo do HEAD 02b4272 sem o fix: timeout 'a sincronização da conta não concluiu', com InvalidServiceLocationException (MercadoLivreItemsGateway não é público, ServiceLocationPolicy.NotAllowed) no log; o comando de red do gate (testes unitários) não cobre IT | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  354ms (environment 59%, transform 19%, import 16%, tests 4%, worker 2%)); lint exit 0 ([0m[34mChecked 40 files in 23[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 97%) — 7ab87f0 | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 731ms) — 7ab87f0 | 2026-10-30 |
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
Os handlers do Listings que recebem `IMercadoLivreItems` (sincronização e notificação) passam a ser gerados pelo Wolverine com o gateway real — antes só funcionavam nos testes, com um dublê público.

### Como foi feito
- `MercadoLivreModule`: `options.CodeGeneration.AlwaysUseServiceLocationFor<IMercadoLivreItems>()`; o gateway continua `internal` e scoped, resolvido no escopo da mensagem.
- Com a importação rodando de verdade depois da conexão, 5 asserções "nenhuma chamada ao ML" dos testes da SPEC-0020 passaram a ignorar só as chamadas da importação (`CallsExceptListingsImport`), removendo um teste instável (Emenda v1.1, `touches`).

### Prova de Correção
- Red: IT-01 (`02b4272`) num clone sem o fix — a importação não conclui; log com `InvalidServiceLocationException` (`MercadoLivreItemsGateway` não é público, `ServiceLocationPolicy.NotAllowed`).
- Green: fix `33f1cbd`; IT-01 passa; suíte 486/486; CI do PR #51 verde.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| IT-01 | Sincronização e notificação com o gateway real (WireMock) gravam e atualizam os anúncios | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |

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
| v1.1 | 2026-10-30 | `touches` inclui `tests/Vitrine.IntegrationTests/MercadoLivre/**`: com a correção, a importação do Listings passa a rodar de verdade depois da conexão nos testes do ML e as asserções "nenhuma chamada ao ML" passam a ignorar as chamadas da importação | Teste instável (`AccountsTests`) revelado pela correção | nenhuma | thomas (piloto automático do épico SPEC-0019) |

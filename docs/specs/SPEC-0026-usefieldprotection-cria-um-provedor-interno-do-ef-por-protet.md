---
id: SPEC-0026
title: UseFieldProtection cria um provedor interno do EF por protetor
tier: lite
type: fix
user_facing: false
status: in-progress
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0020]
consumes_contract: []
touches: [src/Common/Vitrine.Common.Security/**, src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/**, tests/Vitrine.IntegrationTests/Security/**, tests/Vitrine.IntegrationTests/MercadoLivre/**, tests/Vitrine.UnitTests/Security/**]
adrs: [ADR-0006, ADR-0014]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-30
approved_via: piloto automático autorizado no chat em 2026-10-28/30 (épico SPEC-0019)
---

# SPEC-0026 — UseFieldProtection cria um provedor interno do EF por protetor

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
`UseFieldProtection` (SPEC-0008) cria um provedor de serviços interno do EF Core para cada instância de `IFieldProtector`. Em produção há um só protetor (singleton) e tudo funciona; mas quando vários hosts sobem no mesmo processo (testes de integração com `WebApplicationFactory`), o EF passa do limite de 20 provedores internos e emite `ManyServiceProvidersCreatedWarning`, quebrando testes. Por isso a SPEC-0020 (módulo MercadoLivre) não pôde usar `UseFieldProtection`: aplicou um `ValueConverter` próprio, duplicou o `ConverterFor` de `Common.Security` e embrulhou `Wolverine.EntityFrameworkCore.Internals.WolverineModelCacheKeyFactory` numa fábrica de chave de modelo (desvio registrado no G4 da SPEC-0020). Esperado: um único caminho de cifra de campos, sem provedor interno por protetor, que os módulos novos (SPEC-0021, SPEC-0022) possam usar.

## 2. Causa Raiz
`src/Common/Vitrine.Common.Security/FieldProtectionOptionsExtension.cs`: `ExtensionInfo.GetServiceProviderHashCode()` usa a identidade do protetor e `ShouldUseSameServiceProvider` exige o mesmo protetor, porque a convenção captura o protetor no provedor interno. Cada host de teste tem seu próprio protetor → um provedor interno novo por host.

## 3. Mudança Proposta
- O protetor deixa de ser capturado no provedor interno: o hash/igualdade da extensão passa a ser constante, e o modelo é diferenciado por protetor numa fábrica de chave de modelo de `Common.Security` que **compõe** com a fábrica já registrada (a do Wolverine), sem referenciar tipos `Internals` do Wolverine.
- O módulo MercadoLivre volta a usar `UseFieldProtection`/`IsProtected` e remove o conversor e a fábrica locais.
- **Não muda:** formato do texto cifrado, purposes, chaves do Data Protection, colunas e dados existentes (nenhuma migração).

**Padrão seguido:** `FieldProtectionModelExtensions` e `FieldProtectionOptionsExtension` (SPEC-0008); uso nos testes da SPEC-0008 (UT-02, `ProtectedSecretsContext`) — nenhum módulo de produção usa `UseFieldProtection` ainda.

**Rollback:** revert do commit (sem mudança de dados).

**Outras ocorrências:** Nenhuma além do contorno da SPEC-0020, removido aqui.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0026:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — os testes de cifra da SPEC-0008 e da SPEC-0020 (conta conectada e varredura de tokens) já fixam o formato cifrado e a leitura.
- **UT-01** — Dado dois `IFieldProtector` distintos, quando dois contextos com `UseFieldProtection` são criados e usados para gravar e ler, então cada um cifra com o seu protetor e o EF não cria um provedor interno a mais por protetor (contagem de provedores estável).
- **IT-01** — Dado 25 hosts de teste do Vitrine subindo no mesmo processo, quando cada um grava e lê um campo protegido de um contexto com `UseFieldProtection` registrado no host e um do MercadoLivre, então nenhum emite `ManyServiceProvidersCreatedWarning` e os valores voltam decifrados com o protetor do próprio host.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever UT-01 e IT-01 com a tag `SPEC-0026:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `UseFieldProtection` sem provedor interno por protetor, compondo com a fábrica de chave de modelo já registrada; MercadoLivre volta a `UseFieldProtection`/`IsProtected` e remove o contorno local
- [x] Nits do G4 da SPEC-0020: asserção 201+200 na corrida no mesmo workspace e asserção do agendamento recorrente registrado
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
| G0 Spec | PASS | validate: 0 erro(s) — 7718a74 (árvore suja) | 2026-10-30 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 193c8a9 | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  366ms (environment 63%, transform 17%, import 14%, tests 5%, worker 2%)); lint exit 0 ([0m[34mChecked 40 files in 19[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 96%) — 7d3ebc4 | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 532ms) — 7d3ebc4 | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED_WITH_NOTES (0 blocker/major; guarda contra ReplaceService<IModelSource> aceita como risco residual) — 7d3ebc4 | 2026-10-30 |
| G5 Integração & CI | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  359ms (environment 61%, import 17%, transform 16%, tests 5%, worker 2%)); test_integration exit 0 (duration: 2m 18s 059ms); test_e2e exit 0 (30 passed (30.4s)); arch_test exit 0 (duration: 1s 384ms); security_scan exit 0 ([90m10:46AM[0m [32mINF[0m [1mno leaks found[0m); nota: PR #48 CI 8/8 verde + CodeQL (runs/41064867437) — 88cf8e5 | 2026-10-30 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-30 |
| G6 Deploy | PASS | MANUAL: Deploy local prod-like (deploy/deploy.sh 5261c97 --env-file deploy/local/.env): health 200 em 3 s; smoke @smoke 2/2; /api/v1/mercadolivre/accounts sem sessão → 401; 0 logs Error/Critical; staging/produção na SPEC-0009 | 2026-10-30 |
| G7 Pronto & Docs | PENDING | | |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
`UseFieldProtection` (SPEC-0008) passa a usar um único provedor interno do EF Core para todos os protetores, sem estourar o limite de 20 provedores quando vários hosts sobem no mesmo processo. Cada contexto continua cifrando e decifrando com o próprio protetor. O módulo MercadoLivre voltou ao caminho padrão (`UseFieldProtection` + `IsProtected("mercadolivre.tokens")`), sem o contorno local da SPEC-0020.

### Como foi feito
- `src/Common/Vitrine.Common.Security/FieldProtectionOptionsExtension.cs`: hash e igualdade da extensão constantes; `FieldProtectionModelSource` (herda `ModelSource`, registrado com `Replace(IModelSource)`) decora a `IModelCacheKeyFactory` já resolvida (a do EF ou a do Wolverine) com a identidade do protetor na chave do modelo — sem referenciar tipos internos do Wolverine.
- MercadoLivre: removidos o `ValueConverter` local, `ProtectorModelCacheKeyFactory` e a dependência de `Wolverine.EntityFrameworkCore.Internals`. Sem migração; o snapshot do EF não mudou; formato cifrado, purposes, chaves e colunas iguais.
- Guardas herdadas do G4 da SPEC-0020: corrida no mesmo workspace exige 201 + 200; agendamento `CleanupPendingAuthorizations` (`0 3 * * *`, UTC) conferido nas `WolverineOptions` (tags `SPEC-0020:IT-04`/`IT-03`).
- Emenda v1.1: texto do padrão/IT-01 (o Identity não tem campo protegido).
- Risco residual aceito no G4: se alguém chamar `ReplaceService<IModelSource>()`, a separação por protetor some sem aviso (sem efeito em produção, com um protetor por host); nada no repositório ou nas dependências faz isso hoje.

### Prova de Correção
- Red (`0b35a4b`, antes do fix): UT-01 falhou com "5 protetores criaram 5 provedores de serviços internos do EF Core (esperado: 1)"; IT-01 falhou no host 18 de 25 com `ManyServiceProvidersCreatedWarning`. G1 registrado em 193c8a9.
- Green (`7d3ebc4`): UT-01 e IT-01 passam (3/3 e 2/2 execuções no G4); suíte completa 278/278; CI do PR #48 (runs/41064867437) verde.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| UT-01 | Dois ou mais protetores compartilham um provedor interno e cada um cifra/decifra só com o próprio | PASS | CI PR #48 runs/41064867437 (88cf8e5); main 5261c97 |
| IT-01 | 25 hosts no mesmo processo sem `ManyServiceProvidersCreatedWarning`, valores decifrados com o protetor do próprio host | PASS | CI PR #48 runs/41064867437 (88cf8e5); main 5261c97 |

### Definição de Pronto
- [x] Teste de regressão falhou antes e passa depois da correção
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, imagem `ghcr.io/thomasravache/vitrine-api:5261c97`, 2026-10-30, `deploy/deploy.sh` (health 200 em 3 s, smoke 2/2). Staging/produção na SPEC-0009.

### Pendências
Nenhuma.

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 | 2026-10-30 | Texto do padrão e do IT-01: o Identity não tem campo protegido; o IT-01 usa um contexto com `UseFieldProtection` registrado no host | Apontado pelo Test-writer no G1; sem mudança de comportamento | nenhuma | thomas (piloto automático do épico SPEC-0019) |

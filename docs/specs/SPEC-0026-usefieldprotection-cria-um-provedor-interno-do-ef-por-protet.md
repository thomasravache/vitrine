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

**Padrão seguido:** `FieldProtectionModelExtensions` e `FieldProtectionOptionsExtension` (SPEC-0008); uso em `IdentityModuleDbContext`.

**Rollback:** revert do commit (sem mudança de dados).

**Outras ocorrências:** Nenhuma além do contorno da SPEC-0020, removido aqui.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0026:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — os testes da SPEC-0008 (UT-01..03) e da SPEC-0020 (IT-01, IT-07) já fixam o formato cifrado e a leitura.
- **UT-01** — Dado dois `IFieldProtector` distintos, quando dois contextos com `UseFieldProtection` são criados e usados para gravar e ler, então cada um cifra com o seu protetor e o EF não cria um provedor interno a mais por protetor (contagem de provedores estável).
- **IT-01** — Dado 25 hosts de teste do Vitrine subindo no mesmo processo, quando cada um grava e lê um campo protegido do Identity e do MercadoLivre, então nenhum emite `ManyServiceProvidersCreatedWarning` e os valores voltam decifrados com o protetor do próprio host.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever UT-01 e IT-01 com a tag `SPEC-0026:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `UseFieldProtection` sem provedor interno por protetor, compondo com a fábrica de chave de modelo já registrada; MercadoLivre volta a `UseFieldProtection`/`IsProtected` e remove o contorno local
- [ ] Nits do G4 da SPEC-0020: asserção 201+200 na corrida no mesmo workspace e asserção do agendamento recorrente registrado
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
| G0 Spec | PASS | validate: 0 erro(s) — 7718a74 (árvore suja) | 2026-10-30 |
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

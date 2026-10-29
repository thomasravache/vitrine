---
id: SPEC-0016
title: Fixture de integração não libera pools do Postgres compartilhado
tier: lite
type: fix
user_facing: false
status: approved
created: 2026-10-29
parent: SPEC-0001
depends_on: []
consumes_contract: []
touches: [tests/Vitrine.IntegrationTests/Support/**]
adrs: []
external: []
size: S
approved_by: thomas
approved_at: 2026-10-29
approved_via: piloto automático autorizado no chat em 2026-10-29
---

# SPEC-0016 — Fixture de integração não libera pools do Postgres compartilhado

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
A suíte de integração roda perto do limite de conexões do PostgreSQL compartilhado (container do Testcontainers da coleção `PostgresTestGroup`). Cada host de teste (`ApiFactory` ou host próprio) cria um banco com `PostgresFixture.CreateDatabaseAsync`, e os pools do Npgsql (EF Core e Wolverine) mantêm conexões ociosas abertas depois do dispose do host. Na SPEC-0014, 12 hosts a mais bastaram para `53300: sorry, too many clients already` em testes vizinhos (IMP-01 da SPEC-0014; contornado nos próprios testes). Esperado: o descarte de um host libera as conexões do seu banco, e a suíte tem folga para crescer.

## 2. Causa Raiz
`tests/Vitrine.IntegrationTests/Support/ApiFactory.cs` (`DisposeAsync`) e `PostgresFixture.cs`: nenhum dos dois chama `NpgsqlConnection.ClearPool`/`ClearAllPools` para o banco criado, nem limita o pool das connection strings de teste.

## 3. Mudança Proposta
O `ApiFactory` limpa o pool do seu banco ao ser descartado, e o `PostgresFixture` oferece um descarte equivalente para hosts próprios (ou as connection strings de teste usam pool pequeno). Não muda: isolamento de um banco por host, imagem/configuração do container, código de produção, asserts de nenhum teste.

**Padrão seguido:** `PostgresFixture`/`ApiFactory` atuais e a limpeza feita nos testes da SPEC-0014 (`LoginWithoutEmailTests`, `KeyRingRevocationTests`).

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0016:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — a própria suíte de integração completa é a caracterização (tem que continuar verde).
- Unitários: N/A — o defeito está na infraestrutura de teste de integração.
- - **IT-01** — Dado um banco criado pelo `PostgresFixture` e um `ApiFactory` que atendeu requisições, quando o `ApiFactory` é descartado, então `pg_stat_activity` não tem conexões abertas para esse banco; e a suíte de integração completa roda 2x seguidas sem `53300`.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma.

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — fe2b393 (árvore suja) | 2026-10-29 |
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

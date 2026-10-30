---
id: SPEC-0027
title: Teste de arquitetura passa localmente com assembly de módulo não referenciado
tier: lite
type: fix
user_facing: false
status: in-progress
created: 2026-10-30
parent: SPEC-0019
depends_on: []
consumes_contract: []
touches: [tests/Vitrine.ArchitectureTests/**]
adrs: [ADR-0001, ADR-0002]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-30
approved_via: piloto automático autorizado no chat em 2026-10-28/30 (épico SPEC-0019)
---

# SPEC-0027 — Teste de arquitetura passa localmente com assembly de módulo não referenciado

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
O teste de arquitetura `Assemblies_reais_da_solucao_cumprem_todas_as_regras_de_arquitetura` (e os testes por módulo) passam localmente mesmo quando um projeto de `src/` não é referenciado por nenhum outro, mas falham no CI, onde o job `architecture` compila só `tests/Vitrine.ArchitectureTests`. Aconteceu duas vezes neste épico (SPEC-0020: `MercadoLivre.PublicApi`; SPEC-0022: `Listings.IntegrationEvents`), cada uma custando uma rodada de CI e uma correção depois do G4. Esperado: o resultado local é o mesmo do CI.

## 2. Causa Raiz
`tests/Vitrine.ArchitectureTests/Support/SolutionArchitecture.cs` (`LoadProjectAssembly`): quando o assembly não está na saída do projeto de testes, procura em `src/<projeto>/bin/<config>/<tfm>/`. Localmente, depois de um `dotnet build Vitrine.slnx`, esse caminho existe e mascara a falta de referência; no CI, não.

## 3. Mudança Proposta
- O carregamento só aceita assemblies que chegam à saída do próprio projeto de testes (sem o fallback para `src/**/bin`), e a mensagem de erro passa a dizer que o projeto não é referenciado (ex.: "nenhum projeto referencia X; a camada Application do módulo deve referenciar seu IntegrationEvents/PublicApi").
- **Não muda:** as regras de arquitetura, a lista de projetos excluídos e o comando `arch_test`.

**Padrão seguido:** `SolutionArchitecture.cs` e os testes de arquitetura da SPEC-0003.

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0027:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — a suíte de arquitetura atual (18 testes) continua passando e é a guarda.
- **UT-01** — Dado um `.csproj` em `src/` cujo assembly existe só em `src/<projeto>/bin` (não na saída do projeto de testes), quando o carregador de assemblies da solução roda, então falha com mensagem que aponta o projeto não referenciado.
- Integração/E2E: N/A — defeito do próprio harness de testes; o UT-01 o reproduz com um diretório temporário.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever UT-01 com a tag `SPEC-0027:UT-01` e confirmar que falha pelo motivo certo
- [ ] Green: carregador só aceita assemblies na saída do projeto de testes, com mensagem que aponta o projeto não referenciado
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
| G0 Spec | PASS | validate: 0 erro(s) — abb81c8 | 2026-10-30 |
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

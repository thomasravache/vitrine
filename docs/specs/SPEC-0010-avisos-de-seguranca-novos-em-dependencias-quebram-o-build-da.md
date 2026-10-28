---
id: SPEC-0010
title: Avisos de segurança novos em dependências quebram o build da main
tier: lite
type: fix
user_facing: false
status: approved
created: 2026-10-28
parent: SPEC-0001
depends_on: []
consumes_contract: []
touches: [Directory.Packages.props, src/AppHost/Vitrine.AppHost.csproj, src/AppHost/packages.lock.json, tests/Vitrine.Tooling.Tests/**, web/e2e/support/packages.lock.json, web/package.json, web/package-lock.json]
adrs: [ADR-0008]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-28
approved_via: autopiloto autorizado no chat em 2026-10-28 (correção urgente)
---

# SPEC-0010 — Avisos de segurança novos em dependências quebram o build da main

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
A `main` está vermelha desde o merge do PR #13 (run 40779730006, 2026-10-28): o mesmo código que passou no PR em 2026-10-14 agora falha em build-lint, unit, architecture, e2e e security. Esperado: `main` sempre verde (ADR-0005/ADR-0008).

Reprodução: `dotnet build Vitrine.slnx -warnaserror` → `NU1902: Package 'MessagePack' 2.5.302 has a known moderate severity vulnerability` (GHSA-qhrr-8q5h-9q3h); `npm --prefix web audit --audit-level=high` → undici < 7.30.1 e happy-dom ≤ 21.0.3 (alta).

Impacto: nenhum deploy sai da `main` (sem imagem nova) e todo PR fica bloqueado.

## 2. Causa Raiz
Avisos publicados depois do PR (as versões fixadas ficaram vulneráveis com o tempo, não por mudança de código):
- MessagePack 2.5.302 vem transitivo de `Aspire.Hosting` 13.6.1 (AppHost, Tooling.Tests e fixture E2E); a auditoria NuGet com warnings como erro (ADR-0003) quebra o restore. Aspire 13.6.4 depende de MessagePack 2.5.307 (corrigido).
- `undici` 7.29.1, fixado pelo `overrides` de `web/package.json` (dependência do orval), tem aviso alto novo; corrigido em 7.30.1.
- `happy-dom` 20.14.6 (ambiente do Vitest) tem aviso alto (≥17.0.0 ≤21.0.3); corrigido em 21.0.4.

## 3. Mudança Proposta
Muda: Aspire 13.6.1 → 13.6.4 (patch; `Directory.Packages.props` e SDK do AppHost), `overrides.undici` 7.29.1 → 7.30.1, `happy-dom` 20.14.6 → 21.0.4 (dev), lockfiles regenerados. Não muda: código de produção, orval (8.37.0 do ADR-0013), demais dependências — as outras atualizações seguem nos PRs do Dependabot com revisão própria.

**Padrão seguido:** versões só em `Directory.Packages.props` e lockfiles versionados (ADR-0008); teste de arquivo no estilo de `tests/Vitrine.Tooling.Tests/BuildRulesTests.cs`; `overrides` já usado em `web/package.json`

**Rollback:** revert do merge (volta à `main` vermelha; nada em produção)

**Outras ocorrências:** Vai se repetir sempre que sair um aviso novo; a defesa é a rotina semanal do Dependabot (ADR-0008) — PRs #10, #17, #18 e #19 abertos para revisão. Nenhuma spec nova.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0010:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — sem mudança de comportamento; a suíte completa (71 testes .NET, E2E) é a rede de segurança.
- **UT-01** — Dado os lockfiles versionados (`src/AppHost/packages.lock.json`, `tests/Vitrine.Tooling.Tests/packages.lock.json`, `web/e2e/support/packages.lock.json`, `web/package-lock.json`), quando as versões resolvidas são lidas, então nenhuma está nas faixas vulneráveis destes avisos: MessagePack < 2.5.303, undici < 7.30.1, happy-dom ≤ 21.0.3.
- Integração/E2E: N/A — o defeito aparece no build e na auditoria do CI (`build`, `security_scan` do sdd-config), que já reprovam hoje e são a prova no G5.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
- [ ] Red: UT-01 com a tag `SPEC-0010:UT-01`, falhando nos lockfiles atuais
- [ ] Green: atualizar Aspire, undici e happy-dom; regenerar lockfiles
- [ ] Validar: build -warnaserror, suíte completa, E2E, npm audit e scan de vulnerabilidades NuGet (G2/G3)
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e H2
- [ ] Deploy local prod-like com smoke (G6)
- [ ] CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — bf1862a | 2026-10-28 |
| G1 Red | PASS | MANUAL: verify G1 PASS (fc3f2e9); red real: `dotnet test tests/Vitrine.Tooling.Tests -p:NuGetAudit=false -- --filter-trait spec=SPEC-0010:UT-01` → failed 1: MessagePack 2.5.302 em 3 lockfiles, happy-dom 20.14.6, undici 7.29.1 (o red do comando test do sdd-config foi 'vitest not found' — worktree sem node_modules, não conta) | 2026-10-28 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  322ms (environment 60%, import 22%, transform 9%, tests 7%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Installed 6 packages in 8ms) — a0ea53c | 2026-10-28 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 974ms) — a0ea53c | 2026-10-28 |
| G4 Review | PASS | MANUAL: Review de fc3f2e9..a0ea53c: 0 blocker/major; minor (comparação textual de pré-release, sem efeito nas faixas atuais) e nits aceitos; verify --base sdd/SPEC-0001 PASS (1 aviso justificado: lockfile do restore); build -warnaserror 0 avisos; Tooling 13/13; npm audit high exit 0; dotnet list --vulnerable: nenhum; diff de lockfiles: só Aspire 13.6.4, MessagePack 2.5.307, undici 7.30.1, happy-dom 21.0.4 | 2026-10-28 |
| G5 Integração & CI | PASS | MANUAL: PR #23 (sdd/SPEC-0001 @ 1152552): CI 7/7 verde + CodeQL — https://github.com/thomasravache/vitrine/actions/runs/40782044161; local: build -warnaserror 0 avisos, npm audit high 0; implementer: dotnet 72/72, e2e 6/6 | 2026-10-28 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-28 |
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

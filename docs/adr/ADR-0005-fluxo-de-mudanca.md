---
id: ADR-0005
title: Fluxo de mudança — PRs no GitHub com merge commit
status: accepted
origin: decision
date: 2026-10-13
pillars: [fluxo-mudanca]
baseline: fluxo-mudanca@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: proteção da branch main no GitHub + job sdd (pr-check) obrigatório
---

# ADR-0005 — Fluxo de mudança — PRs no GitHub com merge commit

## Contexto e Problema
Push direto e merges locais tiram a revisão e os checks do caminho, e a rastreabilidade entre spec, código e deploy se perde. Parte do código será escrita por agentes; a revisão humana e os checks automáticos são a barreira final.

## Direcionadores da Decisão
- Repositório e CI no GitHub (decisão do usuário); acompanhamento pelas próprias specs (tracker: none).
- Time pequeno: o humano é o revisor obrigatório; agentes nunca aprovam a própria mudança.

## Opções Consideradas
- Trunk-based com PRs curtos, merge commit, branch protection + CODEOWNERS
- Commits diretos na main (change_flow: direct)

## Resultado da Decisão
**Opção escolhida:** "Trunk-based com PRs", porque é o único caminho que garante checks e revisão em toda mudança.

**Regras (verificáveis):**
- Toda mudança entra na branch principal por PR.
- Checks obrigatórios (build-lint, unit, integration, architecture, e2e, security, sdd) verdes antes do merge; 0 aprovações exigidas no GitHub. O merge é feito pelo agente somente com todos os checks verdes **e** G4 (review por agente independente do implementador) = PASS, sob autorização permanente do humano (`h2_policy: auto-on-green`, 2026-10-13); o humano pode revisar qualquer PR a qualquer momento.
- Deploy em produção nunca é coberto pela autorização: confirmação humana por release (GitHub Environment `production`).
- Sem push direto nem force-push na `main`, inclusive para administradores.
- Merge com merge commit (sem squash), para as evidências dos gates continuarem válidas.
- Arquivos de controle (`tools/sdd`, `docs/specs/sdd-config.yml`, `docs/adr`, `.githooks`, `.github/workflows`) têm dono obrigatório (CODEOWNERS: @thomasravache).
- Correção urgente segue o mesmo caminho, com spec lite criada na hora e revisão acelerada.
- Commits em Conventional Commits com `Refs: SPEC-NNNN`; CHANGELOG em Keep a Changelog; versões em SemVer.

### Consequências
- **Boa**, porque nada chega à produção sem checks verdes e revisão.
- **Ruim**, porque a revisão de código passa a ser feita por agente (G4 independente); o risco é mitigado pelos checks obrigatórios, pelo job `sdd` e pela confirmação humana em toda release de produção.

### Confirmação (G3)
Proteção de branch no GitHub; `change_flow: pr` no sdd-config; `vendor --codeowners`; `validate` aponta desvios.

## Prós e Contras das Opções
| Critério (peso) | PRs + proteção | Commits diretos |
|---|---|---|
| Rastreabilidade e checks (5) | 5 | 1 |
| Velocidade solo (2) | 3 | 5 |
| **Total ponderado** | **31** | 15 |

## Mais Informações
Referências: trunk-based development; Conventional Commits 1.0.0; Keep a Changelog 1.1.0; SemVer 2.0.0.
Configuração de proteção de branch e CODEOWNERS: documentação do GitHub (docs.github.com) — não verificado nesta sessão; configurado e conferido na SPEC-0002.

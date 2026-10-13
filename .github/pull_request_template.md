<!--
Título: Conventional Commits — <tipo>(<escopo>)?: <descrição no imperativo> [SPEC-NNNN]
Ex.: feat(wallet): suporta múltiplos cartões [SPEC-0042]
Mudança que quebra compatibilidade: use `!` (ex.: feat(api)!: ...) e preencha "Breaking changes".
Gere o corpo completo a partir da spec: python3 tools/sdd/spec_graph.py pr SPEC-NNNN --out pr.md
Mudança sem comportamento (typo, formatação): marque [no-spec] no título.
-->

## Resumo
<!-- O que muda e por quê, em 2–3 frases. -->

## Specs
<!-- SPEC-NNNN e item do board. -->
- SPEC-

## Como foi feito
<!-- Decisões de implementação e módulos principais. -->

## Arquitetura
<!-- Padrão existente seguido (arquivos de referência), desvios e ADRs aplicados. -->

## Como foi testado
<!-- Unitário, integração, contrato e E2E, com resultado/evidência (execução de CI). -->

## Risco, rollout e rollback
<!-- Estratégia (direto, feature flag, canário), migração de dados, observabilidade, como reverter. -->

## Breaking changes
Nenhuma

## Checklist
- [ ] Spec(s) aprovada(s) (H1) e gates G0–G4 com evidência
- [ ] Testes do plano (unitários, integração, contrato, E2E) passando no CI
- [ ] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [ ] Nenhuma alteração fora do escopo (`touches`) da spec
- [ ] Documentação e CHANGELOG (Keep a Changelog) atualizados quando aplicável
- [ ] Sem segredos ou dados sensíveis no código ou nos logs

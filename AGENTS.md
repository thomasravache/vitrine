# Vitrine — instruções para agentes

Visão do produto, comandos e mapa de pastas: [README.md](README.md). Decisões de arquitetura: `docs/adr/`.

## Comandos (raiz do repositório)

- Build: `dotnet build Vitrine.slnx -warnaserror && npm --prefix web ci && npm --prefix web run build`
- Lint/formatação: `dotnet format Vitrine.slnx --verify-no-changes && npm --prefix web run lint`
  (corrigir: `dotnet format Vitrine.slnx` e `npm --prefix web run lint:fix`)
- Testes: `dotnet test --solution Vitrine.slnx` (IT-01 precisa de Docker)
- Subir tudo localmente: `aspire run`

<!-- sdd:start -->
## Processo de desenvolvimento (SDD)

Este projeto usa Spec-Driven Development. Vale para qualquer mudança de comportamento (feature, bug, refactor, migração), qualquer agente e qualquer modelo:

1. **Carregue a skill `sdd-management` antes de agir.** Sem ela disponível, siga `docs/specs/INDEX.md` e as regras abaixo.
2. **Não dependa da memória da conversa.** Antes de cada passo rode `python3 tools/sdd/spec_graph.py status` (ou `status SPEC-NNNN`) e execute o próximo passo indicado.
3. **Nada é implementado sem spec aprovada pelo humano (H1).** Dúvidas vão para "Questões em Aberto"; paradas, para o "Registro de Impedimentos" (`impede` / `resolve`).
4. **Testes antes do código.** Commit `test(...)` antes de `feat|fix|refactor(...)`. Mensagens em Conventional Commits com rodapé `Refs: SPEC-NNNN`.
5. **Arquitetura, padrões e bibliotecas existentes são mantidos.** Desvio só com spec `type: migration` ou ADR aprovado.
6. **Só o Architect edita `docs/specs/` e `docs/adr/`.** Subagentes reportam; não editam specs.
7. **PRs:** corpo gerado por `spec_graph.py pr SPEC-NNNN`; o CI roda `pr-check` e bloqueia o merge se algo faltar.
8. **Board:** definido em `tracker` no `docs/specs/sdd-config.yml` (`none` = só o repositório).
<!-- sdd:end -->

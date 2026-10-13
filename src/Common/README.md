# src/Common

Building blocks compartilhados do monólito modular ([ADR-0001](../../docs/adr/ADR-0001-arquitetura-e-fronteiras.md)):
abstrações técnicas reutilizadas pelos módulos em `src/Modules/` (ex.: tipos base de domínio, resultado de operação,
abstrações de mensageria). Pasta vazia na fundação; projetos nascem quando uma spec precisar deles.

## Regras

- Sem regra de negócio: o que é de um domínio fica no módulo dono.
- `Common` não referencia nenhum módulo (verificado pelos testes de arquitetura — SPEC-0003).
- Projetos seguem o nome `Vitrine.Common.<Assunto>` e herdam as regras de build da raiz
  (`Directory.Build.props`, `Directory.Packages.props`).

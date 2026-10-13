---
id: ADR-0008
title: Dependências e ciclo de vida da stack
status: accepted
origin: decision
date: 2026-10-13
pillars: [dependencias]
baseline: dependencias@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: security_scan do sdd-config no CI + Dependabot
---

# ADR-0008 — Dependências e ciclo de vida da stack

## Contexto e Problema
Dependências vulneráveis ou fora de suporte viram risco silencioso, e atualizar tudo de uma vez depois fica caro. A stack tem NuGet, npm e imagens Docker; em 2025 bibliotecas populares do .NET (MediatR, MassTransit, AutoMapper) passaram a ter licença comercial, então licença também é risco.

## Direcionadores da Decisão
- Repositório no GitHub; ferramenta nativa e gratuita preferida.
- Três ecossistemas: NuGet, npm, Docker (+ GitHub Actions).

## Opções Consideradas
- Dependabot (version + security updates) + `dotnet list package --vulnerable` + `npm audit` + Trivy na imagem
- Renovate + mesmas varreduras

## Resultado da Decisão
**Opção escolhida:** "Dependabot + varreduras nativas + Trivy", porque é nativo do GitHub, sem app extra, e cobre os quatro ecossistemas.

**Regras (verificáveis):**
- Varredura de vulnerabilidades no CI (`dotnet list package --vulnerable --include-transitive`, `npm audit --audit-level=high`, Trivy na imagem); severidade alta/crítica bloqueia o merge.
- Atualização de dependências automatizada (Dependabot) com revisão, agrupada semanalmente.
- Nenhuma dependência ou runtime fora de suporte (EOL); o calendário de fim de suporte é revisado a cada trimestre e cada item vira spec. Marcos conhecidos: .NET 10 LTS até 2028-11; Node 24 LTS (runtime de build da SPA) até 2028-04; PostgreSQL 18 até 2030-11.
- Arquivos de trava de versão (`packages.lock.json` com `RestorePackagesWithLockFile`, `package-lock.json`) versionados; versões NuGet centralizadas em `Directory.Packages.props`.
- Licenças compatíveis com uso comercial (MIT, Apache-2.0, BSD, PostgreSQL); dependência com licença comercial ou copyleft forte exige ADR.

### Consequências
- **Boa**, porque vulnerabilidades e EOL aparecem em PRs pequenos e semanais.
- **Ruim**, porque gera fluxo constante de PRs de atualização que precisam de revisão.

### Confirmação (G3)
`security_scan` no sdd-config e no CI; `verify` avisa manifesto alterado sem ADR.

## Prós e Contras das Opções
| Critério (peso) | Dependabot | Renovate |
|---|---|---|
| Nativo no GitHub (3) | 5 | 3 |
| Flexibilidade de agrupamento (2) | 3 | 5 |
| **Total ponderado** | **21** | 19 |

## Mais Informações
Datas de fim de suporte: .NET 10 (novembro de 2028, InfoWorld, verificado em 2026-10-13); Node.js 24 e PostgreSQL 18 conforme calendários oficiais (nodejs.org/en/about/previous-releases e postgresql.org/support/versioning) — não verificados nesta sessão, confirmar na SPEC-0002. Referências: SLSA, OpenSSF Scorecard.

---
id: ADR-0003
title: Qualidade de código
status: accepted
origin: decision
date: 2026-10-13
pillars: [qualidade-codigo]
baseline: qualidade-codigo@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: comando lint do sdd-config no CI
---

# ADR-0003 — Qualidade de código

## Contexto e Problema
Estilo e problemas triviais revisados à mão custam caro e geram inconsistência, principalmente quando parte do código é escrita por agentes. O Vitrine tem duas linguagens (C# e TypeScript), cada uma com seu toolchain.

## Direcionadores da Decisão
- Feedback rápido e automático no CI e localmente.
- Configuração versionada, mesma para pessoas e agentes.
- Mínimo de ferramentas distintas.

## Opções Consideradas
- C#: `dotnet format` + .NET analyzers (`AnalysisLevel=latest-recommended`, `TreatWarningsAsErrors`) + nullable; TS: Biome (formatador + linter) + `tsc --noEmit` estrito; SAST: CodeQL
- C#: idem; TS: ESLint + Prettier; SAST: Semgrep

## Resultado da Decisão
**Opção escolhida:** "dotnet format + analyzers / Biome + tsc estrito / CodeQL", porque Biome substitui ESLint+Prettier com uma ferramenta só e CodeQL é gratuito no GitHub para repositórios públicos e coberto pelo GitHub Code Security em privados (custo a confirmar — ver ADR-0012; fallback: Semgrep CE).

**Regras (verificáveis):**
- Formatador com verificação automática no CI (falha se o código não estiver formatado): `dotnet format --verify-no-changes` e `biome ci`.
- Linter com regras versionadas no repositório (`.editorconfig`, `Directory.Build.props`, `biome.json`); zero avisos (warnings são erros).
- Checagem de tipos no modo mais estrito: `<Nullable>enable</Nullable>` em todos os projetos C#; `strict: true` no `tsconfig`.
- Suprimir uma regra exige comentário com o motivo na própria linha; supressão sem motivo é achado do revisor.
- Análise estática de segurança (SAST) no CI.

### Consequências
- **Boa**, porque estilo deixa de ser assunto de revisão e erros de nulidade aparecem na compilação.
- **Ruim**, porque `TreatWarningsAsErrors` torna atualização de SDK/analyzers mais trabalhosa (avisos novos quebram o build).

### Confirmação (G3)
Comando `lint` do sdd-config (`dotnet format --verify-no-changes && dotnet build -warnaserror && cd web && npx biome ci && npx tsc --noEmit`) no CI e no G2; configuração em `.editorconfig`, `Directory.Build.props`, `web/biome.json`, `web/tsconfig.json`.

## Prós e Contras das Opções
| Critério (peso) | Biome + CodeQL | ESLint + Prettier + Semgrep |
|---|---|---|
| Simplicidade (uma ferramenta TS) (4) | 5 | 3 |
| Velocidade (3) | 5 | 3 |
| Riqueza de regras/plugins (2) | 3 | 5 |
| Integração GitHub (3) | 5 | 4 |
| **Total ponderado** | **56** | 43 |

## Mais Informações
Versões verificadas em 2026-10-13: @biomejs/biome 2.5.15; typescript 7.0.2; eslint 10.12.0 (alternativa). Preço/licença do CodeQL em repositório privado: não verificado — confirmar na SPEC-0004.

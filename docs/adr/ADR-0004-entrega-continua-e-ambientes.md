---
id: ADR-0004
title: Entrega contínua e ambientes — GitHub Actions + Docker Compose em VPS
status: accepted
origin: user
date: 2026-10-13
pillars: [entrega]
baseline: entrega@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: pipeline GitHub Actions (deploy-staging, deploy-production) e smoke_test do sdd-config (G6)
---

# ADR-0004 — Entrega contínua e ambientes — GitHub Actions + Docker Compose em VPS

## Contexto e Problema
Sem caminho automatizado até produção, o produto fica "pronto" sem nunca chegar ao usuário, e cada deploy vira evento de risco. O usuário escolheu hospedar em VPS com Docker Compose (origin: user). O Aspire 13 gera `docker-compose.yaml` + `.env` a partir do AppHost (`Aspire.Hosting.Docker`, `aspire publish`), mas não opera o servidor: TLS, atualização do SO, backup e rollback ficam conosco.

## Direcionadores da Decisão
- Escolha do usuário: VPS + Docker Compose (sem teto rígido de orçamento, mas custo baixo é desejável).
- Um único caminho do commit à produção, com rollback em um passo.
- Webhooks do Mercado Livre exigem URL pública HTTPS sempre disponível.

## Opções Consideradas
- **A:** DigitalOcean — droplet (Docker Compose: API + Caddy) + PostgreSQL gerenciado do DigitalOcean (backup e restauração pontual inclusos); staging num droplet menor com Postgres em container.
- **B:** Hetzner Cloud — VPS com tudo em containers, inclusive PostgreSQL, com backup próprio (WAL para object storage).
- **C:** DigitalOcean — tudo em um droplet, Postgres em container, dump diário para Spaces.

## Resultado da Decisão
**Opção escolhida:** "A — DigitalOcean droplet + PostgreSQL gerenciado", porque mantém a escolha do usuário (VPS + Docker Compose para a aplicação) e terceiriza a parte mais arriscada de operar sozinho (backup/restauração do banco). Hetzner perdeu vantagem: dois aumentos de preço em 2026 (abril, até +37–53% nos EUA; junho, linhas CPX/CCX 2,4–3,1x) e linhas baratas marcadas como indisponíveis.

**Desenho:**
- Imagem única `vitrine-api` (API + SPA estática) construída no CI, publicada no GitHub Container Registry (GHCR) com tag = SHA do commit.
- Artefatos de Compose gerados por `aspire publish` (Aspire.Hosting.Docker) e versionados por release; Caddy na frente para HTTPS automático (Let's Encrypt) e cabeçalhos de segurança.
- Ambientes: `staging` (droplet pequeno, Postgres em container, dados descartáveis, conta de teste do Mercado Livre) e `production` (droplet + Postgres gerenciado). Região: NYC (latência para a API do ML, hospedada nos EUA; DigitalOcean não tem região no Brasil).
- Deploy = job do GitHub Actions que, via SSH com chave dedicada por ambiente, grava o `.env` a partir dos GitHub Environment secrets, faz `docker compose pull && docker compose up -d` com a nova tag e espera o health check; falha → volta para a tag anterior automaticamente.
- Migrações de banco executadas pelo pipeline antes da troca de versão (ADR-0011).
- Infraestrutura como código: OpenTofu com provider DigitalOcean para droplets, banco, firewall, DNS e Spaces; cloud-init para o bootstrap do servidor (Docker, usuário de deploy, firewall, atualizações automáticas de segurança).

**Regras (verificáveis):**
- Um único pipeline leva do commit à produção; nada é publicado fora dele.
- A branch principal está sempre em estado publicável.
- Staging é atualizado automaticamente a cada merge, com smoke e E2E contra ele.
- Produção exige confirmação humana por release (GitHub Environment `production` com revisor obrigatório); estratégia direta + feature flags por spec.
- Rollback em um passo (`deploy` da tag anterior), ensaiado na SPEC-0005; mudanças de dados compatíveis com a versão anterior.
- Métricas DORA acompanhadas: frequência de deploy, tempo de entrega, taxa de falha, tempo de recuperação (a partir do histórico de execuções do GitHub Actions).

### Consequências
- **Boa**, porque o custo fica previsível e baixo (~US$ 56/mês, ADR-0012) e o deploy é um comando reproduzível gerado pelo próprio AppHost.
- **Ruim**, porque operamos o servidor: patches do SO, disco, Docker e certificados. Mitigação: atualizações automáticas de segurança, alerta de disco/CPU (ADR-0009) e servidor recriável do zero por IaC.
- **Ruim**, porque um único droplet em produção é ponto único de falha (sem alta disponibilidade). Aceito no lançamento; RTO na ADR-0010. Escalar = droplet maior ou segundo droplet com load balancer.
- **Ruim**, porque o deploy por Docker Compose do Aspire está em preview; por isso o deploy usa `aspire publish` (gerar artefatos) + `docker compose` padrão, e não `aspire deploy`.

### Confirmação (G3)
Pipeline GitHub Actions (`.github/workflows/deploy.yml`); `deploy_staging`, `deploy_production` e `smoke_test` no sdd-config (G6).

## Prós e Contras das Opções
| Critério (peso) | A: DO droplet + PG gerenciado | B: Hetzner tudo próprio | C: DO tudo no droplet |
|---|---|---|---|
| Risco operacional do banco (5) | 5 | 2 | 2 |
| Custo mensal (3) | 4 | 3 (preços instáveis em 2026) | 5 |
| Previsibilidade de preço (3) | 4 | 2 | 4 |
| Simplicidade de IaC (OpenTofu) (2) | 5 | 4 | 5 |
| **Total ponderado** | **59** | 35 | 47 |

## Mais Informações
- Aspire Docker Compose: [aspire.dev/deployment/docker-compose](https://aspire.dev/deployment/docker-compose) — `aspire publish` gera compose + `.env`; deploy via `aspire deploy` em preview. `Aspire.Hosting.Docker` 13.6.1 (NuGet), verificado em 2026-10-13.
- Preços DigitalOcean (fontes secundárias, página oficial não consultada): droplet 2 vCPU/4 GB US$ 24/mês; PostgreSQL gerenciado desde US$ 15/mês; cobrança por segundo desde 2026-01-01 — [infratally](https://infratally.com/articles/digitalocean-droplet-pricing-guide-2026/). Reconfirmar no painel antes do provisionamento (SPEC-0005).
- Hetzner 2026: [findstack](https://findstack.com/resources/hetzner-price-increase-2026), [wz-it](https://wz-it.com/en/blog/hetzner-price-increase-june-2026-cpx-ccx-alternatives/) — verificado em 2026-10-13.
- Referências: DORA (Accelerate), Twelve-Factor App.

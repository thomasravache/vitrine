---
id: SPEC-0001
title: Fundação da plataforma Vitrine
tier: epic
type: foundation
status: proposed
created: 2026-10-13
depends_on: []
adrs: [ADR-0001, ADR-0002, ADR-0003, ADR-0004, ADR-0005, ADR-0006, ADR-0007, ADR-0008, ADR-0009, ADR-0010, ADR-0011, ADR-0012, ADR-0013, ADR-0014, ADR-0015]
external: []
approved_by:
approved_at:
---

# SPEC-0001 — Fundação da plataforma Vitrine (Épico)

## 1. Visão
**Produto:** Vitrine, assistente para vendedores do Mercado Livre (20 a 2.000 anúncios ativos, faturamento a partir de R$ 10 mil/mês, às vezes com mais de uma conta). Ele melhora título, descrição e ficha técnica dos anúncios com aprovação do vendedor e responde perguntas de compradores a qualquer hora, em modo de aprovação ou automático, sem nunca inventar informação. Cobrança por assinatura mensal (R$ 49–199 por faixa de anúncios, a validar) com 7 dias grátis. Meta dos 3 primeiros meses: 30 vendedores pagando, ≥ 70% das respostas sugeridas aprovadas sem edição, anúncios alterados com mais visitas, churn < 10%/mês.

**Este épico** entrega o caminho do código à produção antes de qualquer funcionalidade: repositório e convenções, testes de todos os níveis, CI, ambientes staging e produção numa VPS, observabilidade, segurança de base e um *walking skeleton* (cadastro → login → painel vazio) implantado em produção pelo pipeline. Ao final, cada funcionalidade do produto é um incremento pequeno e seguro sobre um caminho que já funciona.

**Épicos de produto que vêm depois (fora deste épico, specs próprias):** (1) Contas e anúncios — conectar uma ou mais contas ML e listar anúncios; (2) Melhoria de anúncios — sugestão com antes/depois e aprovação (título só quando o ML permite: anúncio sem vendas); (3) Respostas a perguntas — modo aprovação e automático com horários e assuntos que sempre vão ao vendedor, SLA de 2 min; (4) Histórico e rollback; (5) Teste grátis e assinatura. Depois: comparação antes/depois de visitas e vendas; acompanhamento de preços de concorrentes; melhoria em lote.

## 2. Escopo
**Objetivos (dentro do escopo):**
- Repositório GitHub com solução .NET 10 (monólito modular, AppHost Aspire, ServiceDefaults) e SPA React/Vite, convenções e enforcement SDD (SPEC-0002).
- Harness de testes: unitário, integração com PostgreSQL real, contrato, E2E Playwright e testes de arquitetura (SPEC-0003).
- Pipeline de CI no GitHub Actions com checks obrigatórios e imagem publicada no GHCR (SPEC-0004).
- Ambientes staging e produção como código (OpenTofu + cloud-init) na DigitalOcean, deploy por Docker Compose gerado pelo Aspire, migrações pelo pipeline, rollback ensaiado (SPEC-0005).
- Walking skeleton: cadastro, login, logout e painel vazio, com E2E, em staging e produção (SPEC-0006).
- Observabilidade de base: logs estruturados, métricas, traces, erros, health checks, alerta na jornada do skeleton (SPEC-0007).
- Segurança de base: autorização negada por padrão, cabeçalhos de segurança, HTTPS, proteção CSRF, limite de tentativas, cifra de campos sensíveis (SPEC-0008).

**Não-objetivos (fora do escopo):**
- Qualquer integração com o Mercado Livre, IA ou pagamentos (épicos de produto).
- Convite de membros ao workspace, confirmação de e-mail, recuperação de senha e 2FA (épico de produto "Contas e acesso").
- Alta disponibilidade (mais de um servidor de aplicação), app nas lojas, outros marketplaces.

## 3. Arquitetura Alvo
**Contexto:** Projeto novo. Stack e arquitetura definidas nos ADRs de fundação: monólito modular .NET 10 + Aspire 13 (ADR-0001), SPA React/Vite servida na mesma origem (ADR-0013), PostgreSQL + EF Core por módulo (ADR-0011), Wolverine com outbox no PostgreSQL (ADR-0014), Claude Opus 5.5/Sonnet 5.5 nos épicos de produto (ADR-0015), VPS DigitalOcean com Docker Compose (ADR-0004).

```text
Navegador (celular/desktop)
   │ HTTPS
Caddy (TLS, cabeçalhos) ──► vitrine-api (ASP.NET Core 10: módulos + SPA estática + handlers Wolverine)
                               ├─► PostgreSQL (schema por módulo; fila/outbox Wolverine)
                               ├─► OTLP ─► Grafana Cloud    ├─► Sentry
                               └─► (épicos de produto) API Mercado Livre, API Anthropic
GitHub Actions: CI ─► GHCR (imagem por SHA) ─► deploy staging (automático) ─► produção (aprovação humana)
```

Contratos entre as filhas: comandos do repositório e layout de projetos (SPEC-0002); comandos de teste e convenções de tag (SPEC-0003); workflow de CI, nomes de checks e imagem `ghcr.io/thomasravache/vitrine-api:<sha>` (SPEC-0004); workflow de deploy por ambiente e URLs (SPEC-0005); endpoints de autenticação e o módulo Identity (SPEC-0006).

**Decisões (ADRs):** ADR-0001 a ADR-0012 (pilares do perfil padrão), ADR-0013 (frontend), ADR-0014 (mensageria e jobs), ADR-0015 (provedor de IA).

**Regras de arquitetura a garantir (G3):** dependências entre camadas e módulos conforme ADR-0001 (ArchUnitNET); sem ciclos entre módulos; cada `DbContext` mapeia só o próprio schema; endpoints exigem autorização por padrão (ADR-0007); só o adaptador de IA referencia o SDK Anthropic (ADR-0015) — todas em `tests/Vitrine.ArchitectureTests`.

## 4. Decomposição
| Spec | Título | Tier | Tipo | Tamanho | Depende de | Consome contrato de |
|---|---|---|---|---|---|---|
| SPEC-0002 | Repositório, ferramentas e convenções | full | foundation | M | — | — |
| SPEC-0003 | Harness de testes e arquitetura | full | foundation | M | SPEC-0002 | — |
| SPEC-0004 | Pipeline de CI | full | foundation | M | SPEC-0002 | — |
| SPEC-0005 | Ambientes, IaC e deploy em VPS | full | foundation | M | SPEC-0002 | SPEC-0004@1 |
| SPEC-0006 | Walking skeleton: cadastro, login e painel vazio | full | foundation | M | SPEC-0003, SPEC-0004, SPEC-0005 | — |
| SPEC-0007 | Observabilidade de base | full | foundation | M | — | SPEC-0006@1 |
| SPEC-0008 | Segurança de base | full | foundation | M | — | SPEC-0006@1 |

## 5. Estratégia de Entrega
- **Ambientes:** staging atualizado automaticamente a cada merge na `main` (smoke + E2E contra ele); produção pelo mesmo pipeline com aprovação humana no GitHub Environment `production`.
- **Entrega por onda:** ondas 1–2 (repositório, testes, CI, ambientes) não têm usuário final; a onda do skeleton coloca cadastro/login/painel vazio em produção, sem divulgação (URL não anunciada); observabilidade e segurança entram na sequência, antes de qualquer cliente.
- **Feature flags:** N/A no épico de fundação — não há usuário real até o primeiro épico de produto; o cadastro público fica atrás de `Features:SignupOpen` (desligado em produção até o lançamento).
- **Rollback:** redeploy da tag anterior pelo workflow de deploy (um passo, ensaiado na SPEC-0005); migrações compatíveis com a versão anterior.
- **Métricas de sucesso pós-release:** health checks verdes por 7 dias; alerta da jornada de acesso sem disparo falso; tempo de deploy commit→staging < 15 min.

## 6. Riscos & Mitigações
- Deploy por Docker Compose do Aspire em preview — usar `aspire publish` + `docker compose` padrão, não `aspire deploy` (ADR-0004).
- Operar servidor próprio (patches, disco, TLS) — atualizações automáticas, alertas de saturação, servidor recriável por IaC.
- Preços de provedores verificados só em fontes secundárias — reconfirmar no provisionamento (SPEC-0005) e registrar no ADR-0012.
- Revisor humano único — merge autorizado ao agente com CI verde + review independente (G4); produção sempre com confirmação humana.
- Custo de IA alto em relação à receita (ADR-0012) — não afeta a fundação; tratado na spec de planos.

## 7. Critérios de Aceite do Épico
- [ ] Um commit na `main` passa por build, lint, testes (unitário, integração, contrato, E2E, arquitetura), varredura de segurança e o job SDD — SPEC-0004:IT-01
- [ ] Cada merge na `main` é implantado automaticamente em staging e o smoke passa — SPEC-0005:IT-02
- [ ] Rollback para a versão anterior em um passo, ensaiado — SPEC-0005:IT-03
- [ ] Vendedor se cadastra, entra e vê o painel vazio no celular em produção — SPEC-0006:E2E-01
- [ ] Health checks, traces e alerta da jornada de acesso funcionando em produção — SPEC-0007:IT-01
- [ ] Endpoint sem autorização explícita é bloqueado e cabeçalhos de segurança estão presentes — SPEC-0008:IT-01

## 8. Questões em Aberto
- [x] Qual domínio usar para o produto (produção e staging)? — Decidir até a onda 2; registro de um .com.br no Registro.br é pré-requisito externo da SPEC-0005; staging = staging.<domínio> (thomas, 2026-10-13)
- [x] Nome e visibilidade do repositório no GitHub? — `thomasravache/vitrine`, público (Actions e CodeQL gratuitos; código e specs públicos; push protection de segredos do GitHub ligada) (thomas, 2026-10-13)
- [x] Como fica a revisão obrigatória de PR com um único humano no time? — O agente pode aprovar e fazer merge dos PRs desde que passem pelo CI com todos os checks obrigatórios verdes e com G4 (review independente) PASS; registrado como `h2_policy: auto-on-green`. Produção continua exigindo confirmação humana por release (thomas, 2026-10-13)

## 9. Aprovação (H1)
Uma aprovação humana cobre o épico e as specs filhas apresentadas junto com ele. Registrada no frontmatter (`approved_by`, `approved_at`) do épico e de cada filha.

## 10. Registro de Impedimentos
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 11. Relatório de Entrega

## 12. Emendas
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

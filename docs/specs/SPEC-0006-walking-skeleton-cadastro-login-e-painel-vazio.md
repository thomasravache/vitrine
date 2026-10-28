---
id: SPEC-0006
title: "Walking skeleton: cadastro, login e painel vazio"
tier: full
type: foundation
user_facing: true
status: implemented
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0003, SPEC-0004, SPEC-0005]
consumes_contract: []
contract_version: 1
touches: [src/Modules/Identity/**, src/Api/**, web/src/**, web/e2e/**, web/package.json, web/package-lock.json, web/index.html, web/vite.config.ts, tests/README.md, deploy/compose.yaml, deploy/local/.env.example, docs/runbooks/deploy.md, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.ArchitectureTests/**, tests/Vitrine.Tooling.Tests/packages.lock.json, src/AppHost/packages.lock.json, web/e2e/support/packages.lock.json, Vitrine.slnx, Directory.Packages.props, .gitleaksignore, web/playwright.config.ts]
adrs: [ADR-0001, ADR-0007, ADR-0011, ADR-0013, ADR-0014]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-13
---

# SPEC-0006 — Walking skeleton: cadastro, login e painel vazio

## 1. Visão Geral
A fatia mais fina e real do produto, passando por todas as camadas: o vendedor se cadastra (cria usuário e workspace), entra, vê um painel vazio ("Conecte sua conta do Mercado Livre — em breve") e sai. Cria o primeiro módulo (`Identity`) no padrão da ADR-0001, a primeira migração, as primeiras telas mobile-first e o primeiro E2E da jornada crítica `acesso`, implantados em staging e produção pelo pipeline.

## 2. Motivação & Escopo
**Motivação:** provar que a stack, os testes, o CI e o deploy funcionam juntos com código de verdade antes de investir em funcionalidades; a jornada de acesso é pré-requisito de todas as outras.

**Objetivos (dentro do escopo):**
- Módulo `src/Modules/Identity` (Domain, Application, Infrastructure, Presentation, IntegrationEvents) com ASP.NET Core Identity em schema `identity`, entidades `Workspace` e `WorkspaceMember` (papel `Owner`).
- Endpoints de cadastro, login, logout e "quem sou eu" (contrato abaixo), cookie HttpOnly na mesma origem.
- Evento de integração `WorkspaceCreated` publicado via outbox (primeiro uso do Wolverine em produção).
- Telas `/cadastro`, `/entrar`, `/painel` na SPA (React + TanStack Router/Query), responsivas, com cliente gerado do OpenAPI.
- Cadastro público atrás da flag `Features:SignupOpen` (ligada em staging; desligada em produção até o lançamento — no E2E de produção usa-se um usuário de prova criado pelo runbook).
- Primeira migração do schema `identity` executada pelo pipeline.

**Não-objetivos (fora do escopo):**
- Confirmação de e-mail, recuperação de senha, 2FA, login social, convite de membros (épico "Contas e acesso").
- Qualquer conteúdo do painel além do estado vazio.

## 3. Dependências
- **Implementações necessárias:** SPEC-0003 — harness de testes (Postgres, ApiFactory, Playwright, arquitetura); SPEC-0004 — CI; SPEC-0005 — deploy em staging/produção.
- **Contratos consumidos:** N/A
- **Pré-requisitos externos:** N/A

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0001 (estrutura de módulo), ADR-0007 (Identity, cookie, workspace como inquilino), ADR-0011 (EF Core por módulo), ADR-0013 (SPA), ADR-0014 (outbox).

**Decisão:** primeiro módulo segue a estrutura completa de projetos da ADR-0001 e passa a ser a **implementação de referência** para os módulos seguintes; endpoints com Minimal APIs agrupadas por módulo (`MapGroup("/api/v1/auth")`).

**Justificativa:** módulo pequeno o bastante para ser feito numa sessão e completo o bastante para servir de modelo.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** endpoints prontos `MapIdentityApi` (expõem rotas e formatos que não controlamos, sem workspace); Controllers MVC (mais cerimônia que Minimal APIs).

**ADRs:** ADR-0001, ADR-0007, ADR-0011, ADR-0013, ADR-0014.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** p95 < 300 ms nos endpoints de auth no staging (hash de senha incluído) — medido pelas métricas de requisição após a SPEC-0007; antes disso, pelo tempo do E2E.
- **Segurança:** senha mínima de 10 caracteres; bloqueio após 5 falhas por 15 min; mensagem de erro de login genérica (não revela se o e-mail existe); cookie `HttpOnly`, `Secure`, `SameSite=Lax`; antiforgery nos POST — UT-02, IT-03, IT-04, IT-05.
- **Privacidade e dados pessoais:** coleta só nome, e-mail e senha (hash); base legal execução de contrato (ADR-0006); e-mail nunca em log — verificado na SPEC-0007 (teste de mascaramento).
- **Disponibilidade e resiliência:** cadastro é transacional (usuário + workspace + outbox na mesma transação) — IT-01.
- **Acessibilidade (UI):** WCAG 2.2 AA nas três telas: rótulos, foco visível, contraste; axe sem violações sérias/críticas — E2E-01.
- **Custo:** N/A — sem serviço pago novo.

## 6. Artefato A — Contrato
**Interface:** `POST /api/v1/auth/register` · `POST /api/v1/auth/login` · `POST /api/v1/auth/logout` · `GET /api/v1/me` · telas `/cadastro`, `/entrar`, `/painel`

```text
POST /api/v1/auth/register   (anônimo; exige Features:SignupOpen=true; antiforgery)
  body: { name: string(2..100), email: string(email), password: string(10..128) }
  201 -> { userId, workspaceId }  + Set-Cookie de sessão; publica WorkspaceCreated{workspaceId, ownerUserId, createdAt}
  400 VALIDATION_FAILED { errors: { field: [msg] } }
  409 EMAIL_TAKEN
  404 quando SignupOpen=false (rota inexistente para o público)
POST /api/v1/auth/login      (anônimo; antiforgery)
  body: { email, password }
  204 + Set-Cookie
  401 INVALID_CREDENTIALS (mesma resposta para e-mail inexistente e senha errada)
  423 LOCKED_OUT { retryAfterSeconds }
POST /api/v1/auth/logout     (autenticado) -> 204, cookie removido
GET  /api/v1/me              (autenticado) -> 200 { userId, name, email, workspace: { id, name, role } } | 401
GET  /api/v1/auth/antiforgery (anônimo) -> 204 + cookie/cabeçalho de token antiforgery
Erros no formato application/problem+json com campo "code".
Telas: /cadastro e /entrar (formulários com erros por campo); /painel (rota protegida -> redireciona para /entrar se 401)
       painel vazio: saudação com nome + cartão "Conectar conta do Mercado Livre (em breve)" + botão Sair
Evento de integração (contrato público do módulo Identity): WorkspaceCreated v1
```

**Design:** Stitch, projeto "Vitrine" (`projects/6635450613032213900`), sistema de design `assets/15895198957828239862` (primária #0F766E, âmbar #D97706 só para pendências, Plus Jakarta Sans + Inter). Telas mobile: Criar Conta (`screens/5f1d4aa64d00423db0ff9fbc9eb1ee3b`), Entrar (`screens/5b966849f44c438a9e615291c1462194`), Painel vazio (`screens/790582561ed742e3b6f29e7cc7173a8e`). Telas desktop: Painel vazio (`screens/180baf7c767043d2a71a5eb5a2849b42`), Entrar (`screens/580775212cbe49e693f7fd3002ac7463`); Criar Conta desktop segue o mesmo cartão centralizado de Entrar. Desktop (≥ 1024 px): menu lateral no lugar da navegação inferior, conteúdo centralizado com largura máxima de 1120 px; os formulários de auth ficam num cartão centralizado. Diferenças em relação aos desenhos, por escopo: sem selo "teste grátis" no painel e sem "7 dias grátis" no botão (o botão diz "Criar conta"; teste grátis é do épico de assinatura); sem link "Ajuda" nem botão voltar no topo de Entrar; "Esqueci minha senha" visível como "em breve" e desabilitado; texto de Termos/Privacidade sem link até a spec jurídica; o cartão "operar 100% pelo celular" do painel não entra (o produto é responsivo). No painel desktop não entram: "Configurações", "Ajuda", sino de notificações, subtítulo "Gestão ML", links "Auditoria completa por catálogo"/"Menor tempo de resposta" e as promessas "análise da qualidade das fotos" e "dados reais de estoque" (fora do escopo do produto e contrárias à regra de nunca afirmar o que o anúncio não contém); os cartões usam os textos curtos da versão mobile. Em Entrar desktop não entra o selo "Conexão criptografada e segura".

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Cadastro com sucesso | dados válidos, SignupOpen=true | 201; usuário + workspace (Owner) criados; sessão iniciada; `WorkspaceCreated` no outbox | UT-01, IT-01, E2E-01 |
| Cadastro inválido | e-mail mal formado, senha < 10, nome vazio | 400 VALIDATION_FAILED com erros por campo; mensagens no formulário | UT-02, IT-02 |
| E-mail já usado | e-mail existente | 409 EMAIL_TAKEN | IT-02 |
| Cadastro fechado | SignupOpen=false | 404 | IT-06 |
| Login com sucesso | credenciais válidas | 204 + cookie; painel acessível | IT-03, E2E-01 |
| Credenciais inválidas | senha errada ou e-mail inexistente | 401 INVALID_CREDENTIALS idêntico nos dois casos | IT-03 |
| Bloqueio | 5 falhas seguidas | 423 LOCKED_OUT com retryAfterSeconds | UT-03, IT-04 |
| Sem antiforgery | POST sem token | 400 | IT-05 |
| Painel sem sessão | GET /api/v1/me ou /painel sem cookie | 401; SPA redireciona para /entrar | IT-07, E2E-02 |
| Logout | POST /logout | 204; /me volta 401 | IT-07, E2E-01 |
| Isolamento | usuário A consulta /me | só vê o próprio workspace | IT-08 |
| Evento publicado | cadastro concluído | consumidor de teste recebe `WorkspaceCreated` v1 com campos do contrato | CT-01 |
| Fronteiras | módulo Identity | respeita regras de arquitetura | UT-04 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado um novo cadastro, quando `Workspace.CreateFor(user)` é chamado, então o workspace tem um único membro com papel `Owner` e gera o evento de domínio de criação.
- **UT-02** — Dado comandos de cadastro com nome vazio, e-mail inválido ou senha com 9 caracteres, quando validados, então cada campo inválido gera seu erro.
- **UT-03** — Dado a configuração de Identity, quando lida, então `MaxFailedAccessAttempts=5` e `DefaultLockoutTimeSpan=15 min`.
- **UT-04** — Dado os assemblies do módulo Identity, quando as regras de arquitetura rodam, então passam (camadas, isolamento, schema próprio).
- **UT-05** — Dado o `BASE_URL` do E2E, quando a configuração do Playwright decide se aceita certificado não confiável, então só aceita para `localhost` e hosts `*.localhost` (CA interna do Caddy local); para staging/produção (ex.: `https://staging.vitrine.app`), hosts parecidos (`vitrine.localhost.evil.com`) ou sem `BASE_URL`, o certificado continua validado.

### 7.3 Testes de Integração
- **IT-01** — Dado Postgres real, quando `POST /register` com dados válidos, então 201, usuário, workspace e membro Owner persistidos, cookie de sessão emitido e mensagem `WorkspaceCreated` registrada no outbox na mesma transação.
- **IT-02** — Dado Postgres real, quando `POST /register` com dados inválidos ou e-mail existente, então 400 com erros por campo ou 409 EMAIL_TAKEN, e nada é persistido.
- **IT-03** — Dado um usuário existente, quando `POST /login` com a senha correta, então 204 + cookie `HttpOnly; Secure; SameSite=Lax`; com senha errada ou e-mail inexistente, então 401 com corpo idêntico.
- **IT-04** — Dado um usuário existente, quando 5 logins falham seguidos, então o 6º retorna 423 com `retryAfterSeconds` > 0.
- **IT-05** — Dado qualquer POST de auth sem token antiforgery válido, quando enviado, então 400.
- **IT-06** — Dado `Features:SignupOpen=false`, quando `POST /register`, então 404.
- **IT-07** — Dado sessão ativa, quando `POST /logout`, então 204 e `GET /me` passa a retornar 401; sem cookie, `GET /me` retorna 401.
- **IT-08** — Dado dois usuários em workspaces distintos, quando cada um chama `GET /me`, então cada resposta contém apenas o próprio workspace.

### 7.4 Testes de Contrato
- **CT-01** — Dado um cadastro concluído, quando o handler de teste assinante de `WorkspaceCreated` recebe a mensagem, então o payload valida contra o schema v1 publicado em `Identity.IntegrationEvents` (`workspaceId`, `ownerUserId`, `createdAt`).

### 7.5 Testes E2E
- **E2E-01** — Dado o navegador em viewport de celular, quando o vendedor se cadastra em `/cadastro`, vê o painel com seu nome e o cartão "Conectar conta do Mercado Livre (em breve)", sai, e entra de novo em `/entrar`, então volta ao painel; axe sem violações sérias/críticas nas três telas [jornada: acesso] @smoke (no ambiente local prod-like da SPEC-0005; em produção a jornada é provada pela SPEC-0009:E2E-01).
- **E2E-02** — Dado nenhuma sessão, quando o usuário abre `/painel`, então é redirecionado para `/entrar`.

### 7.6 Outros
- Acessibilidade: @axe-core/playwright dentro do E2E-01 (critério: zero violações sérias/críticas).

**Dublês e dados de teste:** builders de usuário com e-mails únicos por teste; usuário de prova de produção criado pelo runbook com senha em secret do Environment.

**Ambiente de execução:** IT com Testcontainers (local + CI); E2E contra AppHost (local + CI) e contra staging/produção no deploy (`@smoke`).

## 8. Plano de Rollout
- **Estratégia:** feature flag `Features:SignupOpen` — ligada em staging, desligada em produção até o lançamento público.
- **Dados/schema:** migração inicial do schema `identity` (tabelas do ASP.NET Core Identity + `workspaces` + `workspace_members`) e tabelas do Wolverine; criação apenas (expand), sem dado prévio.
- **Compatibilidade:** primeira versão da API (`/api/v1`).
- **Observabilidade:** contadores de cadastros, logins com sucesso/falha e bloqueios (métricas expostas na SPEC-0007); logs sem e-mail/senha.
- **Rollback:** redeploy da tag anterior; a migração inicial pode permanecer (versão anterior não usa as tabelas).
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Domínio e aplicação**
- [x] Red: escrever UT-01..UT-04 com a tag `SPEC-0006:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: módulo `Identity` (Domain, Application, Infrastructure, Presentation, IntegrationEvents), `Workspace`/`WorkspaceMember`, validações, configuração de lockout
- [x] Refactor mantendo tudo verde

**Fase 2: API, persistência e evento**
- [x] Red: escrever IT-01..IT-08 e CT-01 e confirmar que falham pelo motivo certo
- [x] Green: endpoints `/api/v1/auth/*` e `/api/v1/me`, cookie, antiforgery, flag `Features:SignupOpen`, migração do schema `identity`, `WorkspaceCreated` via outbox
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase 3: Jornada E2E**
- [x] Red: E2E-01, E2E-02 falhando pelo motivo certo
- [x] Green: telas `/cadastro`, `/entrar`, `/painel` conforme o Stitch (mobile e desktop) e as exclusões da seção Design; cliente gerado do OpenAPI; axe sem violações sérias

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like (SPEC-0005) com smoke/E2E; nuvem e usuário de prova na SPEC-0009 (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — 4602cbe (árvore suja) | 2026-10-13 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 88a35f3 | 2026-10-14 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  311ms (environment 59%, import 23%, transform 10%, tests 7%, worker 1%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (AVISO: uvx não encontrado; diff-cover (>= 80% das linhas alteradas) NÃO foi verificado loc) — 858646b | 2026-10-14 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 028ms) — 858646b | 2026-10-14 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: reviewer-agent: APPROVED 858646b — sem blocker/major; minor: guarda de /health desconhecido não exercita o fallback da SPA (host de teste sem index.html) → levado à SPEC-0008 — 858646b | 2026-10-14 |
| G5 Integração & CI | PASS | MANUAL: PR #13 (sdd/SPEC-0001 @ 65176c8): CI 7/7 verde + CodeQL — https://github.com/thomasravache/vitrine/actions/runs/38636314099; local: build/lint OK, dotnet 71/71, vitest 1/1, e2e 6/6, pr-check PASS, gitleaks 0, npm audit 0 | 2026-10-14 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-28 |
| G6 Deploy | PASS | smoke_test exit 0 (2 passed (3.7s)) — 80e611d | 2026-10-28 |
| G7 Pronto & Docs | PASS | Relatório de Entrega e Definição de Pronto: ok — 593df48 | 2026-10-28 |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-14 | G2 | spec | TEST_DEFECT: IdentityDatabase.CountWorkspaceCreatedEnvelopesAsync faz convert_from(body,'UTF8') no envelope binário do Wolverine (IT-01 falha com 22021) | Implementer validou correção position(convert_to(@workspace,'UTF8') in body) localmente e reverteu | Test-writer (correção do helper) | Emenda v1.1 — helper do IT-01 corrigido pelo Test-writer (cdb1a6d), IntegrationTests 25/25 | 2026-10-14 |

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
<!-- comportamento entregue do ponto de vista do usuário/sistema -->
Primeira jornada ponta a ponta do Vitrine, no celular e no desktop: o vendedor cria a conta (nome, e-mail, senha de 10 a 128 caracteres), cai no painel vazio com o nome dele e o workspace criado, sai e entra de novo. Sem sessão, qualquer rota protegida leva para `/entrar`. Cadastro controlado pela flag `Features:SignupOpen` por ambiente. Cada cadastro publica o evento `WorkspaceCreated` pela outbox, base para os próximos módulos.

### Como foi feito
<!-- decisões de implementação, módulos/arquivos principais, desvios e emendas (com versão), dívidas assumidas -->
Módulo `Identity` (Domain, Application, Infrastructure, Presentation, IntegrationEvents) no monólito modular (ADR-0001), ASP.NET Core Identity com cookie de sessão e antiforgery, schema `identity` no PostgreSQL com migração só via `--migrate`, Wolverine com outbox transacional para `WorkspaceCreated`. SPA React com rotas `/cadastro`, `/entrar`, `/painel`, cliente TypeScript gerado do OpenAPI pelo orval 8.37.0 e fontes auto-hospedadas (ADR-0013, aprovado em 2026-10-14), telas seguindo o Stitch. Emendas: v1.1 (helper de teste), v1.2 (touches + flag por ambiente), v1.3 (senha 10..128), v1.4 (`.gitleaksignore` por fingerprint para senhas fixas de teste), v1.5 (E2E aceita a CA interna do Caddy só em `*.localhost`, guardado pelo UT-05). Desvios aceitos no G4 e levados à SPEC-0008: forwarded headers/antiforgery `Secure=Always`, health anônimo por metadado e teste do fallback da SPA. Dívida: `undici` fixado por `overrides` (orval) — revisar quando o orval atualizar.

### Prova de Correção
<!-- type fix: o teste de regressão falhou antes da correção (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->
N/A — type foundation.

### Verificação
<!-- Uma linha por teste do plano (todos os IDs da seção 7). Resultado: PASS. Evidência: execução de CI, commit ou relatório. -->
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| UT-01 | Workspace nasce com um único membro Owner e evento de criação | PASS | CI PR #13 run 38636314099 (65176c8) |
| UT-02 | Validação de cadastro: nome, e-mail e senha 10..128 | PASS | CI PR #13 run 38636314099 (65176c8) |
| UT-03 | Bloqueio após 5 falhas por 15 min configurado | PASS | CI PR #13 run 38636314099 (65176c8) |
| UT-04 | Regras de arquitetura do módulo Identity | PASS | CI PR #13 run 38636314099 (65176c8); G3 858646b |
| UT-05 | Certificado não confiável só aceito em hosts .localhost (24 casos) | PASS | CI PR #26 run 40784247371 + main run 40785554764 (80e611d) |
| IT-01 | Cadastro 201 com usuário, workspace, Owner e outbox na mesma transação | PASS | CI PR #13 run 38636314099 (65176c8) |
| IT-02 | Cadastro inválido ou e-mail repetido → 400 por campo | PASS | CI PR #13 run 38636314099 (65176c8) |
| IT-03 | Login 204 + cookie HttpOnly/Secure/SameSite=Lax; erro genérico | PASS | CI PR #13 run 38636314099 (65176c8) |
| IT-04 | 6º login após 5 falhas → 423 com retryAfterSeconds | PASS | CI PR #13 run 38636314099 (65176c8) |
| IT-05 | POST de auth sem antiforgery → 400 | PASS | CI PR #13 run 38636314099 (65176c8) |
| IT-06 | Cadastro fechado (SignupOpen=false) → 404 | PASS | CI PR #13 run 38636314099 (65176c8) |
| IT-07 | Logout 204 e /me passa a 401 | PASS | CI PR #13 run 38636314099 (65176c8) |
| IT-08 | /me isolado por workspace | PASS | CI PR #13 run 38636314099 (65176c8) |
| CT-01 | Assinante recebe WorkspaceCreated com o contrato publicado | PASS | CI PR #13 run 38636314099 (65176c8) |
| E2E-01 | Jornada cadastro → painel → sair → entrar, celular e desktop, axe sem violações sérias @smoke | PASS | CI PR #13 run 38636314099 (65176c8); G6 smoke em https://vitrine.localhost (80e611d) |
| E2E-02 | Sem sessão, /painel redireciona para /entrar | PASS | CI PR #13 run 38636314099 (65176c8) |

### Definição de Pronto
- [x] Todos os testes do plano passando e listados na Verificação
- [x] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [x] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [x] Requisitos não-funcionais medidos com evidência (ou N/A justificado) — segurança (UT-02, IT-03..05), transação (IT-01), acessibilidade axe (E2E-01); p95 de auth e e-mail fora dos logs: medidos na SPEC-0007, como o plano define (antes disso, jornada E2E ~4–6 s com hash de senha)
- [x] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6)
- [x] Observabilidade e rollback prontos conforme o Plano de Rollout — rollback = redeploy da tag anterior (ensaiado pelo IT-01 da SPEC-0005); métricas de auth são entregues pela SPEC-0007, como o plano define
- [x] Documentação raiz e CHANGELOG atualizados (G7)
- [x] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->
Ambiente local prod-like (staging até a SPEC-0009): `deploy/deploy.sh 80e611d00c98b58ae8467880eee53bff8026e6f0 --env-file deploy/local/.env`, imagem `ghcr.io/thomasravache/vitrine-api` publicada pela CI da `main` (run 40785554764), Caddy + PostgreSQL via Docker Compose, health 200 em 2 s, `FEATURES_SIGNUP_OPEN=true`; smoke `@smoke` 2/2 (desktop e mobile) em 2026-10-28. Produção: SPEC-0009, com a flag desligada até o lançamento.

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->
- SPEC-0011 — checagem de drift do cliente OpenAPI no CI.
- SPEC-0008 — forwarded headers, antiforgery `Secure=Always`, health anônimo por metadado e teste do fallback da SPA (escopo já aprovado).
- SPEC-0007 — métricas de auth, p95 e mascaramento de e-mail nos logs.

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0006`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| Emenda v1.3 (detalhe de validação, sem mudança de versão) | 2026-10-14 | Senha com máximo de 128 caracteres; cadastro concorrente com mesmo e-mail responde 409; erros do framework trazem `code`; `/health/*` desconhecido responde 404 — todos cobertos por testes de guarda adicionados após o green | Review G4 | — | Architect (sem mudança de versão) |
| Emenda v1.2 (escopo, sem mudança de contrato) | 2026-10-14 | touches incluem `tests/README.md`, `deploy/compose.yaml`, `deploy/local/.env.example` e `docs/runbooks/deploy.md` para ligar `Features:SignupOpen` por ambiente (local prod-like) e documentar | Review G4: o Plano de Rollout pede a flag ligada fora de produção e o E2E-01 roda no ambiente local no G6 | — | Architect (sem mudança de contrato) |
| Emenda v1.1 (correção de teste, sem mudança de contrato) | 2026-10-14 | Helper do IT-01 busca o workspaceId nos bytes do corpo binário do envelope do Wolverine; `web/vite.config.ts` nos touches (proxy /api no desenvolvimento) | IMP-01 (TEST_DEFECT) e necessidade do proxy para a SPA alcançar a API na mesma origem sob o AppHost | — | Architect (sem mudança de contrato) |
| v1 (sem mudança de contrato) | 2026-10-13 | Smoke de produção e usuário de prova movidos para a SPEC-0009; G6 no ambiente local prod-like | Usuário: nada que gere custo até tudo rodar localmente | SPEC-0009 | thomas (2026-10-13) |
| v1.4 (escopo, sem mudança de contrato) | 2026-10-14 | `touches` ganha `.gitleaksignore` com os 3 fingerprints exatos das senhas fixas de teste (regra generic-api-key) | CI do PR #13: gitleaks acusou falsos positivos em `acesso.spec.ts`, `TestUser.cs` e `RegisterCommandValidatorTests.cs` | — | thomas (2026-10-28, opção A: exceção por fingerprint) |
| v1.5 (escopo, sem mudança de contrato) | 2026-10-28 | `touches` ganha `web/playwright.config.ts`: `ignoreHTTPSErrors` só quando o host do `BASE_URL` termina em `.localhost` (CA interna do Caddy local); staging e produção seguem validando o certificado; guarda UT-05 | G6 FAIL: smoke @smoke contra https://vitrine.localhost deu `ERR_CERT_AUTHORITY_INVALID` (primeiro E2E contra ambiente implantado) | — | thomas (2026-10-28, piloto automático) |

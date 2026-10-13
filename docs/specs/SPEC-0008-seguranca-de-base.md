---
id: SPEC-0008
title: Segurança de base
tier: full
type: foundation
user_facing: false
status: proposed
created: 2026-10-13
parent: SPEC-0001
depends_on: [SPEC-0006]
consumes_contract: [SPEC-0006@1]
contract_version: 1
touches: [src/Api/Security/**, src/Api/Program.cs, src/Common/Security/**, tests/Vitrine.UnitTests/Security/**, tests/Vitrine.IntegrationTests/Security/**, tests/Vitrine.ArchitectureTests/**, deploy/Caddyfile, docs/runbooks/**]
adrs: [ADR-0007, ADR-0006]
external: []
size: M
approved_by:
approved_at:
---

# SPEC-0008 — Segurança de base

## 1. Visão Geral
Fecha a base de segurança antes de qualquer dado de cliente: autorização negada por padrão verificada por teste de arquitetura, cabeçalhos de segurança e HTTPS estrito, limite de requisições nos endpoints de autenticação, serviço de cifra de campos sensíveis (usado pelos tokens do Mercado Livre nos épicos seguintes), persistência das chaves de Data Protection e verificação de que segredos e dados sensíveis não vazam.

## 2. Motivação & Escopo
**Motivação:** o Vitrine vai guardar tokens que alteram anúncios de terceiros; as proteções transversais precisam existir antes do primeiro token.

**Objetivos (dentro do escopo):**
- `FallbackPolicy = RequireAuthenticatedUser` e lista explícita de endpoints anônimos; teste de arquitetura que falha se surgir endpoint anônimo fora da lista.
- Cabeçalhos: `Strict-Transport-Security` (produção), `Content-Security-Policy` restritiva para a SPA, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, `X-Frame-Options: DENY`; remoção de `Server`/`X-Powered-By`.
- Rate limiting (`Microsoft.AspNetCore.RateLimiting`) em `/api/v1/auth/*`: 10 req/min por IP e 5 req/min por e-mail no login; resposta 429 com `Retry-After`.
- `IFieldProtector` em `src/Common` (AES-256-GCM via Data Protection com propósito por campo) + conversor EF Core `Protected<T>`; chaves do Data Protection persistidas no PostgreSQL e protegidas por chave mestra vinda do ambiente.
- Configuração do `.env`/Environment para a chave mestra; procedimento de rotação no runbook.
- `security_scan` completo (gitleaks + dependências + Trivy) já no CI (SPEC-0004) — aqui só os ajustes de regra se necessário.

**Não-objetivos (fora do escopo):**
- OAuth com o Mercado Livre (épico "Contas e anúncios"); 2FA; WAF.

## 3. Dependências
- **Implementações necessárias:** SPEC-0006 — os testes de integração exercitam os endpoints de autenticação reais.
- **Contratos consumidos:** SPEC-0006@1 — endpoints de auth (rate limiting, antiforgery) e telas da SPA (CSP).
- **Pré-requisitos externos:** chave mestra gerada pelo responsável, guardada como secret dos Environments e em cofre pessoal fora do servidor (ADR-0006).

## 4. Decisão Arquitetural
**Contexto:** Projeto novo. Padrão definido em ADR-0007 (autorização negada por padrão, cookie na mesma origem) e ADR-0006 (segredos e cifra de campos).

**Decisão:** proteções nativas do ASP.NET Core 10 (autorização, rate limiting, Data Protection) configuradas no host; cifra de campo como building block em `src/Common`, sem dependência de módulos.

**Justificativa:** sem bibliotecas novas; tudo verificável por testes de integração.

**Desvio do padrão existente:** Nenhum (projeto novo).

**Alternativas descartadas:** cifra no banco (pgcrypto) — a chave trafegaria para o banco; cofre externo (ADR-0006 descartou).

**ADRs:** ADR-0007, ADR-0006.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** cifra/decifra de um campo < 1 ms (p95) — UT-03 com medição simples; rate limiting sem impacto perceptível.
- **Segurança:** OWASP Top 10 aplicáveis: A01 (controle de acesso — IT-01, UT-01), A02 (falhas criptográficas — UT-02, IT-04), A05 (configuração — IT-02), A07 (autenticação — IT-03) — checklist no G4.
- **Privacidade e dados pessoais:** valores protegidos nunca são logados (o conversor não implementa `ToString` com conteúdo) — UT-02.
- **Disponibilidade e resiliência:** reinício do container não invalida sessões nem dados cifrados (chaves persistidas) — IT-05.
- **Acessibilidade (UI):** N/A — sem tela nova.
- **Custo:** N/A

## 6. Artefato A — Contrato
**Interface:** política de autorização, cabeçalhos HTTP, limites de requisição e `IFieldProtector`

```text
Autorização: todo endpoint exige usuário autenticado, exceto a lista AnonymousEndpoints:
  POST /api/v1/auth/register, POST /api/v1/auth/login, GET /api/v1/auth/antiforgery, GET /health/live, GET /health/ready, arquivos estáticos da SPA
Cabeçalhos em toda resposta: Content-Security-Policy (default-src 'self'; script-src 'self'; connect-src 'self' <sentry>; frame-ancestors 'none'),
  X-Content-Type-Options: nosniff, Referrer-Policy: strict-origin-when-cross-origin, Permissions-Policy: camera=(), microphone=(), geolocation=(),
  X-Frame-Options: DENY; em produção também Strict-Transport-Security: max-age=31536000; includeSubDomains
Rate limit /api/v1/auth/*: 10/min por IP; login 5/min por e-mail -> 429 + Retry-After
src/Common/Security:
  interface IFieldProtector { string Protect(string plaintext, string purpose); string Unprotect(string ciphertext, string purpose); }
  EF Core: builder.Property(x => x.Secret).IsProtected("<purpose>")   // grava cifrado, lê decifrado
  Erro: CryptographicException -> nunca devolve texto parcial
Config: DataProtection__MasterKey (secret do Environment)
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches` no frontmatter.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Endpoint sem política | novo endpoint sem `RequireAuthorization` nem na lista anônima | teste de arquitetura falha | UT-01 |
| Acesso anônimo negado | GET de rota protegida sem cookie | 401 | IT-01 |
| Cabeçalhos | qualquer resposta | todos os cabeçalhos do contrato presentes; `Server` ausente | IT-02 |
| Rate limit | 11ª requisição/min do mesmo IP em auth | 429 + Retry-After | IT-03 |
| Cifra de campo | valor protegido gravado | coluna no banco não contém o texto claro; leitura devolve o original | UT-02, IT-04 |
| Propósito errado / adulteração | decifrar com propósito diferente ou texto alterado | CryptographicException | UT-03 |
| Reinício | container reiniciado | sessão e dados cifrados continuam válidos | IT-05 |
| Contrato de auth | endpoints SPEC-0006@1 | login/registro funcionam sob CSP, antiforgery e rate limit | CT-01 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- N/A — projeto novo.

### 7.2 Testes Unitários
- **UT-01** — Dado a lista de endpoints do host (`EndpointDataSource`) e uma rota de amostra sem política, quando a regra "autorização por padrão" roda, então falha apontando a rota; sem a rota de amostra, passa.
- **UT-02** — Dado um `IFieldProtector`, quando `Protect("token-123","ml-token")` é chamado, então o resultado difere do texto claro, `Unprotect` devolve `"token-123"`, e o valor protegido não aparece em `ToString()` da entidade.
- **UT-03** — Dado um texto protegido, quando decifrado com propósito diferente ou com um byte alterado, então lança `CryptographicException`; e 1.000 operações de cifra+decifra têm p95 < 1 ms.

### 7.3 Testes de Integração
- **IT-01** — Dado a API, quando cada rota protegida conhecida é chamada sem cookie, então 401; e cada rota da lista anônima responde sem exigir login.
- **IT-02** — Dado a API em ambiente `Production` simulado, quando `GET /` e `GET /api/v1/me`, então as respostas trazem todos os cabeçalhos do contrato (incluindo HSTS) e não trazem `Server`.
- **IT-03** — Dado a API, quando 11 requisições de auth do mesmo IP chegam em um minuto, então a 11ª recebe 429 com `Retry-After`; e 6 tentativas de login para o mesmo e-mail de IPs diferentes resultam em 429 na 6ª.
- **IT-04** — Dado uma entidade de teste com campo `IsProtected`, quando gravada no Postgres real, então a coluna lida por SQL cru não contém o texto claro e a leitura via EF devolve o original.
- **IT-05** — Dado sessão ativa e um campo cifrado gravado, quando o host é reiniciado com a mesma chave mestra, então a sessão continua válida e o campo é decifrado.

### 7.4 Testes de Contrato
- **CT-01** — Dado o contrato SPEC-0006@1, quando cadastro e login são executados pelo cliente gerado da SPA sob as novas políticas (CSP, rate limit, antiforgery), então os códigos de resposta continuam os do contrato (201/204/401/409/423).

### 7.5 Testes E2E
- N/A — `user_facing: false`: nenhuma jornada muda; SPEC-0006:E2E-01 roda sob a CSP nova no CI e no deploy e falha se a CSP quebrar a SPA.

### 7.6 Outros
- Varredura de cabeçalhos em staging (Mozilla Observatory ou `curl -I` scriptado) com nota A ou superior — evidência no G6.

**Dublês e dados de teste:** entidade de teste só no projeto de testes; chave mestra de teste gerada por execução.

**Ambiente de execução:** local + CI (Testcontainers); varredura de cabeçalhos em staging.

## 8. Plano de Rollout
- **Estratégia:** deploy direto — ainda sem clientes.
- **Dados/schema:** nova tabela de chaves do Data Protection (expand); sem dado prévio.
- **Compatibilidade:** CSP pode quebrar scripts de terceiros na SPA — coberto pelo E2E da SPEC-0006 rodando sob a CSP.
- **Observabilidade:** contagem de respostas 429 e 401 nas métricas de requisição (SPEC-0007).
- **Rollback:** redeploy da tag anterior; a tabela de chaves permanece (sem efeito na versão anterior).
- **Etapas de migração/coexistência:** N/A

## 9. Questões em Aberto
Nenhuma

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PENDING | | |
| G1 Red | PENDING | | |
| G2 Green | PENDING | | |
| G3 Arquitetura | PENDING | | |
| G4 Review | PENDING | | |
| G5 Integração & CI | PENDING | | |
| H2 Integração aprovada | PENDING | | |
| G6 Deploy | PENDING | | |
| G7 Pronto & Docs | PENDING | | |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
<!-- comportamento entregue do ponto de vista do usuário/sistema -->

### Como foi feito
<!-- decisões de implementação, módulos/arquivos principais, desvios e emendas (com versão), dívidas assumidas -->

### Prova de Correção
<!-- type fix: o teste de regressão falhou antes da correção (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->

### Verificação
<!-- Uma linha por teste do plano (todos os IDs da seção 7). Resultado: PASS. Evidência: execução de CI, commit ou relatório. -->
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|

### Definição de Pronto
- [ ] Todos os testes do plano passando e listados na Verificação
- [ ] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [ ] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [ ] Review independente sem achados blocker/major (G4)
- [ ] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [ ] Requisitos não-funcionais medidos com evidência (ou N/A justificado)
- [ ] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6)
- [ ] Observabilidade e rollback prontos conforme o Plano de Rollout
- [ ] Documentação raiz e CHANGELOG atualizados (G7)
- [ ] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0008`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

---
id: ADR-0007
title: Identidade e acesso — ASP.NET Core Identity com cookie na mesma origem e workspaces
status: accepted
origin: decision
date: 2026-10-13
pillars: [seguranca-acesso]
baseline: seguranca-acesso@2026.09
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de arquitetura "endpoints exigem autorização por padrão" + testes de acesso negado por spec
---

# ADR-0007 — Identidade e acesso — ASP.NET Core Identity com cookie na mesma origem e workspaces

## Contexto e Problema
Autenticação e autorização improvisadas em cada tela ou endpoint geram brechas difíceis de achar depois. No Vitrine: o dono e um assistente podem cuidar dos mesmos anúncios, e um cliente pode ter várias contas do Mercado Livre. Dados de um cliente nunca podem vazar para outro (multi-inquilino). Além do login no Vitrine, existe a autorização OAuth 2.0 do Mercado Livre para conectar contas — são coisas diferentes.

## Direcionadores da Decisão
- SPA servida na mesma origem da API (ADR-0001/0013): cookie HttpOnly é mais seguro que token no navegador.
- Sem custo por usuário ativo e sem serviço extra para operar na VPS.
- Isolamento entre clientes verificado por teste.

## Opções Consideradas
- ASP.NET Core Identity (cookie HttpOnly, SameSite=Lax, mesma origem) com e-mail/senha e confirmação de e-mail; login social Google depois
- Keycloak auto-hospedado (OIDC)
- Provedor gerenciado (Auth0, Clerk)

## Resultado da Decisão
**Opção escolhida:** "ASP.NET Core Identity com cookie na mesma origem", porque é nativo do .NET 10, não tem custo por usuário, não acrescenta serviço à VPS e o modelo de cookie na mesma origem elimina tokens no navegador.

**Modelo de inquilino:** `Workspace` é o inquilino. Usuários são membros de um workspace com papel `Owner` ou `Member` (assistente); contas do Mercado Livre e assinatura pertencem ao workspace. No lançamento, cada cadastro cria um workspace com um Owner; convite de membros fica para o épico de produto.

**Regras (verificáveis):**
- Autenticação centralizada no ASP.NET Core Identity; senhas com o hasher padrão (PBKDF2, iterações padrão do .NET 10); política mínima de 10 caracteres + verificação contra senhas vazadas (lista k-anônima) quando disponível; bloqueio após 5 tentativas.
- Autorização negada por padrão: `FallbackPolicy = RequireAuthenticatedUser`; endpoints públicos marcados explicitamente com `AllowAnonymous` (lista revisada no G4).
- Todo acesso a dados de negócio é filtrado pelo `WorkspaceId` do usuário autenticado (filtro global de consulta no EF Core por módulo); acessar recurso de outro workspace retorna 404.
- Menor privilégio para usuários, serviços e credenciais de CI (token de deploy restrito por ambiente).
- Sessões com expiração (14 dias deslizantes) e revogação (troca de senha invalida sessões via security stamp); proteção CSRF (antiforgery) nos endpoints que mudam estado.
- O plano de testes de cada spec com regra de acesso inclui o caso de acesso negado e o caso "outro workspace".
- Os riscos do OWASP Top 10 aplicáveis são checados na revisão (G4).
- Conexão de conta ML usa OAuth 2.0 Authorization Code com PKCE e `state` validado; escopos mínimos necessários.

### Consequências
- **Boa**, porque não há custo nem dependência externa para login, e o isolamento entre clientes é regra de código testada.
- **Ruim**, porque fluxos como recuperação de senha, confirmação de e-mail e 2FA são nossos para testar e manter; requer provedor de e-mail transacional (decidido na SPEC-0006).

### Confirmação (G3)
Teste de arquitetura: todo endpoint mapeado tem política de autorização ou `AllowAnonymous` explícito em lista permitida; testes de integração de acesso negado e de isolamento entre workspaces nas specs; checklist de segurança no G4.

## Prós e Contras das Opções
| Critério (peso) | ASP.NET Core Identity | Keycloak | Auth0/Clerk |
|---|---|---|---|
| Custo (3) | 5 | 4 | 2 |
| Operação na VPS (4) | 5 | 2 | 5 |
| Segurança do modelo SPA (cookie mesma origem) (4) | 5 | 4 | 4 |
| Recursos prontos (SSO, 2FA, social) (2) | 3 | 5 | 5 |
| **Total ponderado** | **61** | 46 | 52 |

## Mais Informações
Referências: OWASP ASVS 5.0 (V6 Authentication, V8 Authorization); OWASP Top 10 2025; RFC 7636 (PKCE). Documentação do ASP.NET Core Identity (.NET 10): não verificado nesta sessão (framework 10.0 conferido pelo SDK local 10.0.401 em 2026-10-13). Fluxo OAuth do Mercado Livre: developers.mercadolivre.com.br (a página retornou 403 para leitura automatizada; confirmar escopos na spec de conexão de contas).

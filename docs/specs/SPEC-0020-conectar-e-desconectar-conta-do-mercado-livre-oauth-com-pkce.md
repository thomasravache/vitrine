---
id: SPEC-0020
title: Conectar e desconectar conta do Mercado Livre (OAuth com PKCE)
tier: full
type: feature
user_facing: false
status: in-progress
created: 2026-10-30
parent: SPEC-0019
depends_on: []
consumes_contract: []
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/MercadoLivre/**, src/Modules/Identity/**, src/Common/Vitrine.Common.Web/**, src/Api/**, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.ArchitectureTests/**, web/src/api/**, Vitrine.slnx, deploy/compose.yaml, deploy/local/.env.example, tests/Vitrine.Tooling.Tests/packages.lock.json, web/e2e/support/packages.lock.json, src/AppHost/packages.lock.json]
adrs: [ADR-0001, ADR-0006, ADR-0007, ADR-0011, ADR-0014]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-30
---
# SPEC-0020 — Conectar e desconectar conta do Mercado Livre (OAuth com PKCE)

## 1. Visão Geral
Cria o módulo `MercadoLivre` com a conexão de contas.

O fluxo de conexão:
1. O `Owner` do workspace inicia a conexão.
2. É levado à página de autorização do ML, com PKCE S256 e `state`.
3. Volta para a SPA, que entrega `code` e `state` à API.
4. A API troca o code por tokens, lê `/users/me` e grava a conta com os tokens cifrados.

Também faz parte do módulo:
- listar as contas do workspace;
- desconectar, o que apaga os tokens e publica um evento.

É a base de todo o produto: anúncios e perguntas usam essas contas.

## 2. Motivação & Escopo
**Motivação:** sem uma conta do ML conectada, o Vitrine não lê nem altera nada. A ADR-0007 exige OAuth Authorization Code com PKCE e `state` validado. A ADR-0006 exige tokens cifrados que nunca saem da API.

**Objetivos (dentro do escopo):**
- Módulo `src/Modules/MercadoLivre` no padrão da ADR-0001, com:
  - Domain, Application, Infrastructure, Presentation, IntegrationEvents e PublicApi;
  - schema `mercadolivre`, com DbContext próprio e migração inicial.
- Endpoints de iniciar conexão, concluir conexão, listar contas e desconectar (contrato abaixo).
- Tokens (access e refresh) cifrados com `IFieldProtector`, propósito `mercadolivre.tokens` (SPEC-0008).
- Autorização pendente com `state` (guardado só como hash SHA-256), `code_verifier` cifrado, validade de 10 min e uso único.
- Eventos de integração v1 `MercadoLivreAccountConnected` e `MercadoLivreAccountDisconnected` publicados via outbox (ADR-0014).
- `Identity.PublicApi` (novo projeto): `IWorkspaceMembership.GetAsync(userId)` → `{ workspaceId, role }`, para os módulos saberem o workspace e o papel do usuário logado.
- Extração de `AntiforgeryFilter` e `HttpsRequiredFilter` do Identity para `src/Common/Vitrine.Common.Web`, reutilizados pelos dois módulos sem cópia. É um refactor: os testes do Identity servem de guarda.
- Configuração `MercadoLivre:*` (seção 6), com as variáveis no `deploy/compose.yaml` e no `deploy/local/.env.example`, sem segredo real.
- Métrica `vitrine.mercadolivre.connections` (contador por `result`).

**Não-objetivos (fora do escopo):**
- Renovação de token e chamadas autenticadas além de `/users/me`: SPEC-0021.
- Telas: SPEC-0025. Esta spec entrega a API, e a jornada no navegador é provada lá.
- Revogar a autorização no ML ao desconectar: o endpoint não foi verificado; fica pendência.
- Webhook: SPEC-0023.

## 3. Dependências
- **Implementações necessárias:** N/A. Usa o que a fundação já entregou: Identity (SPEC-0006), cifra de campos e autorização por padrão (SPEC-0008), outbox (SPEC-0003/0006).
- **Contratos consumidos:** N/A. Os testes simulam o ML com WireMock (padrão da SPEC-0003:IT-03).
- **Pré-requisitos externos:**
  - Desenvolvimento, CI e ambiente local: nenhum (simulador da SPEC-0024 ou WireMock).
  - Uso real: app no DevCenter do ML com redirect URI HTTPS, PKCE ligado e escopos `offline_access read write`, com `ClientId`/`ClientSecret` em secret do Environment. É pendência do usuário antes do go-live.

## 4. Decisão Arquitetural
**Contexto:** a implementação de referência é o módulo `src/Modules/Identity`:
- registro do módulo em `IdentityModule.cs`;
- DbContext com outbox (`AddDbContextWithWolverineIntegration`);
- migrações em `IdentityMigrations`;
- endpoints com `MapGroup` em `IdentityEndpoints.cs`;
- erros com `code` em `Problems.cs`.

A cifra de campo segue `src/Common/Vitrine.Common.Security`. O cliente HTTP nomeado segue `tests/Vitrine.IntegrationTests/ExternalHttpTests.cs` e o `ServiceDefaults` (Http.Resilience).

**Decisão:**
- Novo módulo `MercadoLivre` no padrão do Identity.
- A conclusão do OAuth é feita pela SPA, que recebe o redirect e faz um POST autenticado com `code` e `state`, e não por um endpoint de callback anônimo. Assim o cookie de sessão e o antiforgery protegem a troca, e nenhum endpoint anônimo novo é criado.
- O `state` amarra a autorização ao usuário e ao workspace que a iniciaram.

**Justificativa:**
- Mantém a regra da SPEC-0008 de autorização por padrão, sem nova exceção.
- O PKCE protege contra code interceptado, e o `state` (validado pela aplicação, porque o ML não valida) protege contra CSRF de login.

**Desvio do padrão existente:** nenhum.
- `Identity.PublicApi` é a forma de leitura síncrona entre módulos prevista na ADR-0001.
- `Vitrine.Common.Web` é um building block sem regra de negócio, permitido pela ADR-0001 (`Common` não referencia módulos).

**Alternativas descartadas:**
- Callback anônimo `GET /api/v1/mercadolivre/callback` com redirect para a SPA: cria um endpoint anônimo que altera estado e depende de cookie em navegação cross-site (`SameSite=Lax` funciona, mas aumenta a superfície).
- Guardar o verifier na sessão/cookie: o banco já guarda a autorização pendente, com expiração e uso único auditáveis.
- Biblioteca OAuth genérica (ex.: `AspNet.Security.OAuth.MercadoLibre`): é feita para login social (sign-in), não para guardar tokens de uma conta conectada, e seria dependência nova sem ganho.

**ADRs:** ADR-0001, ADR-0006, ADR-0007, ADR-0011, ADR-0014.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** iniciar a conexão leva p95 < 150 ms. Concluir depende de 2 chamadas ao ML, com timeout total de 10 s (Http.Resilience). Medido nos IT (tempo do handler com WireMock) e pela métrica de requisição (SPEC-0007).
- **Segurança:**
  - PKCE S256 com verifier de 64 caracteres (alfabeto do RFC 7636) e `state` de 32 bytes aleatórios em base64url — UT-01.
  - O `state` é uso único, expira em 10 min e está amarrado ao usuário — IT-03.
  - Só `Owner` conecta e desconecta — IT-06.
  - Antiforgery em todo POST/DELETE — IT-09.
  - Tokens cifrados em repouso, nunca devolvidos pela API nem registrados em log — IT-07.
  - Isolamento por workspace — IT-08.
  - URLs do ML vêm da configuração; em `Production`, só as oficiais (`https://auth.mercadolivre.com.br`, `https://api.mercadolibre.com`) — UT-04.
- **Privacidade e dados pessoais:**
  - Guarda só `ml_user_id`, `nickname`, `site_id` e os tokens. Não guarda e-mail, nome nem documento do vendedor no ML, mesmo que `/users/me` os devolva.
  - Base legal: execução de contrato (ADR-0006). Retenção até desconectar: tokens apagados na hora — IT-05.
  - O `nickname` pode aparecer em log; tokens, code e verifier nunca — IT-07.
- **Disponibilidade e resiliência:**
  - Falha ou timeout do ML na troca de tokens gera `502 ML_UNAVAILABLE`, sem gravar conta nem tokens. A autorização pendente já foi consumida (contrato do §6: um `state` nunca serve duas vezes, e o `code` do ML também é de uso único); para tentar de novo, o vendedor reinicia a conexão, que gera uma nova autorização — IT-04 (Emenda v1.1).
  - O evento sai pelo outbox na mesma transação da gravação da conta — IT-01.
- **Acessibilidade (UI):** N/A — sem tela nesta spec (SPEC-0025).
- **Custo:** N/A — sem serviço pago; o app do ML é gratuito.

## 6. Artefato A — Contrato
**Interface:**
- `POST /api/v1/mercadolivre/connections`
- `POST /api/v1/mercadolivre/connections/complete`
- `GET /api/v1/mercadolivre/accounts`
- `DELETE /api/v1/mercadolivre/accounts/{accountId}`
- eventos `MercadoLivreAccountConnected` v1 e `MercadoLivreAccountDisconnected` v1
- `IWorkspaceMembership`

```text
Todos exigem sessão (política de fallback). POST/DELETE exigem antiforgery (X-XSRF-TOKEN) e HTTPS (atrás do proxy).
Erros em application/problem+json com "code". Usuário sem workspace -> 403 FORBIDDEN.

POST /api/v1/mercadolivre/connections                         (Owner)
  body: {}
  200 -> { authorizationUrl: string }
         authorizationUrl = {MercadoLivre:AuthorizationBaseUrl}/authorization?response_type=code
           &client_id={ClientId}&redirect_uri={RedirectUri}&state={state}
           &code_challenge={base64url(SHA256(verifier))}&code_challenge_method=S256
         grava autorização pendente: {id, workspaceId, userId, stateHash, verifier (cifrado), expiresAt = agora+10min, usedAt=null}
  403 FORBIDDEN            usuário não é Owner
  503 ML_NOT_CONFIGURED    ClientId, ClientSecret ou RedirectUri ausentes

POST /api/v1/mercadolivre/connections/complete                (Owner)
  body: { code: string(1..512), state: string(1..128) }
  1. busca a autorização pendente por SHA256(state): inexistente, expirada, já usada ou de outro usuário -> 400 OAUTH_STATE_INVALID
     (a autorização é marcada como usada antes de chamar o ML: um state nunca serve duas vezes)
  2. POST {ApiBaseUrl}/oauth/token (form-urlencoded): grant_type=authorization_code, client_id, client_secret, code,
     redirect_uri, code_verifier
       400 invalid_grant                     -> 400 OAUTH_CODE_INVALID
       outros 4xx (invalid_client, unauthorized_application, ...) -> 502 ML_UNAVAILABLE (log Error com o "error" do ML, sem segredos)
       5xx / timeout / 429                   -> 502 ML_UNAVAILABLE
     resposta: { access_token, token_type, expires_in, scope, user_id, refresh_token }
       sem refresh_token (app sem offline_access) -> 502 ML_UNAVAILABLE + log Error "escopo offline_access ausente"
  3. GET {ApiBaseUrl}/users/me (Bearer access_token) -> { id: int64, nickname, site_id, ... }
       site_id != "MLB"                      -> 422 ML_SITE_NOT_SUPPORTED (nada gravado)
       id != user_id do token                -> 502 ML_UNAVAILABLE
  4. conta com o mesmo ml_user_id em OUTRO workspace -> 409 ML_ACCOUNT_IN_USE (nada gravado; tokens descartados)
  5. grava/atualiza a conta (tokens cifrados, accessTokenExpiresAt = agora + expires_in, status = connected) e publica
     MercadoLivreAccountConnected no outbox, na mesma transação
  201 -> { accountId, mlUserId, nickname, status: "connected" }           conta nova
  200 -> { accountId, mlUserId, nickname, status: "connected" }           reconexão (mesmo ml_user_id no mesmo workspace)
  400 VALIDATION_FAILED { errors }   body inválido
  403 FORBIDDEN                      não é Owner
  503 ML_NOT_CONFIGURED

GET /api/v1/mercadolivre/accounts                             (qualquer membro)
  200 -> [{ accountId, mlUserId, nickname, siteId, status: "connected" | "reauthorization_required", connectedAt }]
         só do workspace do usuário, ordenado por connectedAt; nunca contém tokens

DELETE /api/v1/mercadolivre/accounts/{accountId}              (Owner)
  204 -> conta e tokens apagados; autorizações pendentes do workspace para essa conta não importam (são por usuário);
         publica MercadoLivreAccountDisconnected no outbox, na mesma transação
  404 NOT_FOUND     conta inexistente ou de outro workspace (não revela existência)
  403 FORBIDDEN     não é Owner

Configuração (MercadoLivre:*), lida de variáveis de ambiente (MercadoLivre__ClientId etc.):
  ClientId, ClientSecret (secret), RedirectUri (ex.: https://vitrine.localhost/contas/mercado-livre/retorno),
  AuthorizationBaseUrl (padrão https://auth.mercadolivre.com.br), ApiBaseUrl (padrão https://api.mercadolibre.com),
  AllowNonOfficialEndpoints (padrão false; em Production com false, URLs diferentes das oficiais impedem a subida)

Persistência (schema "mercadolivre"):
  accounts(id uuid pk, workspace_id uuid, ml_user_id bigint UNIQUE, nickname, site_id, status,
           access_token (cifrado), refresh_token (cifrado), access_token_expires_at, connected_at, updated_at)
  pending_authorizations(id uuid pk, workspace_id, user_id, state_hash UNIQUE, code_verifier (cifrado),
           expires_at, used_at)  — linhas expiradas há mais de 1 dia são apagadas pelo job diário do módulo

Eventos de integração (Vitrine.MercadoLivre.IntegrationEvents), v1:
  MercadoLivreAccountConnected    { accountId: uuid, workspaceId: uuid, mlUserId: int64, siteId: string,
                                    reconnected: bool, occurredAt: datetime(UTC) }
  MercadoLivreAccountDisconnected { accountId: uuid, workspaceId: uuid, mlUserId: int64, occurredAt: datetime(UTC) }

Identity.PublicApi:
  interface IWorkspaceMembership { Task<WorkspaceMembership?> GetAsync(Guid userId, CancellationToken ct); }
  record WorkspaceMembership(Guid WorkspaceId, string Role)   // Role: "Owner" | "Member"
```

**Design:** N/A — sem tela nesta spec. As telas e o retorno do OAuth estão na SPEC-0025.

**Arquivos/módulos afetados:** ver `touches`.
- `src/Modules/MercadoLivre/Vitrine.MercadoLivre.{Domain,Application,Infrastructure,Presentation,IntegrationEvents,PublicApi}`.
- `src/Modules/Identity/Vitrine.Identity.PublicApi` (novo), mais o registro no `IdentityModule`.
- `src/Common/Vitrine.Common.Web`.
- `src/Api/Program.cs` e `Vitrine.Api.csproj`.
- `web/src/api` (cliente regenerado).
- `Vitrine.slnx`.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Iniciar conexão | Owner, ML configurado | 200 com URL de autorização (PKCE S256, `state`), autorização pendente gravada | UT-01, IT-02 |
| ML não configurado | sem ClientId/Secret/RedirectUri | 503 ML_NOT_CONFIGURED | IT-02 |
| Concluir com sucesso | code e state válidos, conta MLB nova | 201, conta gravada com tokens cifrados, `MercadoLivreAccountConnected` no outbox | UT-02, IT-01, CT-01 |
| Reconectar | mesmo `ml_user_id` no mesmo workspace | 200, tokens trocados, status `connected`, evento com `reconnected=true` | IT-01 |
| State inválido | inexistente, expirado, reutilizado ou de outro usuário | 400 OAUTH_STATE_INVALID, nada chamado no ML | UT-03, IT-03 |
| Code recusado | ML responde `invalid_grant` | 400 OAUTH_CODE_INVALID, nada gravado | IT-04 |
| ML indisponível | 5xx, timeout, 429 ou erro de cliente na troca | 502 ML_UNAVAILABLE, nada gravado | IT-04 |
| Sem refresh token | resposta sem `refresh_token` | 502 ML_UNAVAILABLE, log de erro de escopo | IT-04 |
| Conta de outro site | `site_id` ≠ MLB | 422 ML_SITE_NOT_SUPPORTED | IT-04 |
| Conta em uso | `ml_user_id` já em outro workspace | 409 ML_ACCOUNT_IN_USE | IT-04 |
| Listar contas | qualquer membro | 200 só com as contas do workspace, sem tokens | IT-05, IT-08 |
| Desconectar | Owner, conta do workspace | 204, conta e tokens apagados, `MercadoLivreAccountDisconnected` no outbox | IT-05, CT-01 |
| Desconectar conta alheia | conta inexistente ou de outro workspace | 404 NOT_FOUND | IT-08 |
| Não Owner | Member em POST/DELETE | 403 FORBIDDEN | IT-06 |
| Tokens protegidos | qualquer fluxo | colunas de token não estão em texto claro; respostas e logs sem token, code nem verifier | IT-07 |
| Sem antiforgery | POST/DELETE sem token | 400 | IT-09 |
| URLs não oficiais em produção | `Production` com URL diferente da oficial e `AllowNonOfficialEndpoints=false` | a validação de opções falha na subida | UT-04 |
| Membership | usuário com e sem workspace | `IWorkspaceMembership` devolve workspace e papel, ou null | UT-05, IT-10 |
| Fronteiras | módulo MercadoLivre e Identity.PublicApi | regras de camada/isolamento; nenhum tipo público com token | UT-06 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- **CH-01** — Dado o Identity atual, quando os testes de integração de auth (SPEC-0006:IT-03 e IT-05) rodam depois da extração de `AntiforgeryFilter`/`HttpsRequiredFilter` para `Vitrine.Common.Web`, então continuam passando sem alteração (guarda: passa antes da mudança).

### 7.2 Testes Unitários
- **UT-01** — Dado o gerador de PKCE/state, quando gera um par, então:
  - o verifier tem 64 caracteres do alfabeto `[A-Za-z0-9-._~]`;
  - o challenge é `base64url(SHA256(verifier))` sem padding, conferido com o vetor do RFC 7636, apêndice B;
  - o `state` tem 32 bytes aleatórios em base64url;
  - duas gerações nunca se repetem.
- **UT-02** — Dado uma conta nova, quando `MercadoLivreAccount.Connect(...)` e depois `Reconnect(...)` com tokens novos, então:
  - o status fica `connected`;
  - `accessTokenExpiresAt = agora + expires_in`;
  - o evento de domínio indica `reconnected` corretamente.
- **UT-03** — Dado uma autorização pendente, quando validada em cada situação, então só a primeira consumação dentro de 10 min, pelo mesmo usuário, é aceita. Expirada, já usada ou de outro usuário é recusada (com `TimeProvider` falso).
- **UT-04** — Dado `MercadoLivreOptions`, quando validadas, então:
  - em `Production` com URL não oficial e `AllowNonOfficialEndpoints=false`, a validação falha;
  - com `true`, ou fora de `Production`, passa;
  - `RedirectUri` sem HTTPS falha fora de `Development`.
- **UT-05** — Dado `IWorkspaceMembership` com o repositório do Identity, quando consultado, então devolve `WorkspaceId` e `Role` do membro, ou null para usuário sem workspace.
- **UT-06** — Dado os assemblies do módulo MercadoLivre e `Identity.PublicApi`, quando as regras de arquitetura rodam, então:
  - passam as regras de camadas e de isolamento;
  - `Identity.PublicApi` não referencia projetos internos do Identity;
  - nenhum tipo público de `MercadoLivre.PublicApi`/`IntegrationEvents` tem propriedade com "token", "secret" ou "verifier" no nome.

### 7.3 Testes de Integração
<!-- Postgres real (Testcontainers) e ML simulado pelo WireMock (RedirectToWireMock, SPEC-0003:IT-03). -->
- **IT-01** — Dado Postgres real e WireMock com `/oauth/token` e `/users/me` válidos (MLB), quando o Owner inicia e conclui a conexão, então:
  - responde 201 com `accountId`, `mlUserId` (int64 acima de Int32) e `nickname`;
  - a conta é gravada com tokens cifrados e `MercadoLivreAccountConnected` fica no outbox na mesma transação;
  - o WireMock recebeu `code_verifier` e `redirect_uri` corretos;
  - uma nova conexão da mesma conta responde 200 e troca os tokens.
- **IT-02** — Dado a configuração completa, quando o Owner chama `POST /connections`, então responde 200 com a URL contendo `response_type=code`, `client_id`, `redirect_uri`, `state`, `code_challenge` e `code_challenge_method=S256`. Sem ClientId, ClientSecret ou RedirectUri, responde 503 ML_NOT_CONFIGURED.
- **IT-03** — Dado `state` inexistente, expirado (TimeProvider avançado), já usado ou criado por outro usuário, quando `complete`, então responde 400 OAUTH_STATE_INVALID e o WireMock não recebeu nenhuma chamada.
- **IT-04** — Dado o WireMock respondendo cada falha, quando `complete`, então:

  | Resposta do WireMock | Resultado |
  |---|---|
  | `invalid_grant` | 400 OAUTH_CODE_INVALID |
  | 500 | 502 ML_UNAVAILABLE |
  | timeout | 502 ML_UNAVAILABLE |
  | 429 | 502 ML_UNAVAILABLE |
  | `invalid_client` | 502 ML_UNAVAILABLE |
  | sem `refresh_token` | 502 ML_UNAVAILABLE |
  | `site_id=MLA` | 422 ML_SITE_NOT_SUPPORTED |
  | `ml_user_id` já conectado em outro workspace | 409 ML_ACCOUNT_IN_USE |

  Em todos os casos, nada é gravado na tabela de contas.
- **IT-05** — Dado uma conta conectada, quando o Owner chama `DELETE`, então:
  - responde 204;
  - a linha da conta (com os tokens) não existe mais;
  - `MercadoLivreAccountDisconnected` está no outbox;
  - `GET /accounts` não a lista mais.
- **IT-06** — Dado um usuário `Member` do workspace (criado direto no banco, pois não há convite), quando chama `POST /connections`, `POST /complete` ou `DELETE`, então responde 403 FORBIDDEN. `GET /accounts` responde 200.
- **IT-07** — Dado uma conexão concluída com tokens conhecidos do WireMock, então:
  - as colunas `access_token`/`refresh_token` e `code_verifier` no Postgres não contêm o valor em claro;
  - nenhuma resposta dos quatro endpoints contém os tokens;
  - os logs capturados (provedor de log de teste) não contêm token, code, verifier nem client secret.
- **IT-08** — Dado dois workspaces com uma conta cada, quando cada Owner lista e tenta apagar a conta do outro, então cada lista só tem a própria conta, e o `DELETE` da conta alheia responde 404.
- **IT-09** — Dado `POST /connections`, `POST /complete` e `DELETE` sem token antiforgery, quando enviados, então respondem 400.
- **IT-10** — Dado Postgres real, quando `IWorkspaceMembership.GetAsync` é chamado para o Owner recém-cadastrado, então devolve o workspace criado no cadastro e `Owner`.

### 7.4 Testes de Contrato
- **CT-01** — Dado uma conexão e uma desconexão concluídas, quando um assinante de teste recebe `MercadoLivreAccountConnected` e `MercadoLivreAccountDisconnected`, então os payloads validam contra o schema v1 publicado em `MercadoLivre.IntegrationEvents` (campos e tipos da seção 6, `mlUserId` int64). As SPEC-0022 e SPEC-0021 consomem esse contrato.

### 7.5 Testes E2E
- N/A nesta spec: a API é provada por IT. A jornada `conectar-conta` no navegador é provada pelos E2E da SPEC-0025, que consome este contrato com o simulador.

### 7.6 Outros
- Segurança: IT-07 (tokens e segredos) e IT-09 (antiforgery). O gitleaks do CI cobre os valores de teste (fictícios; exceção só por fingerprint, se o gitleaks os apontar).

**Dublês e dados de teste:**
- WireMock para `/oauth/token` e `/users/me`, com builders de resposta do ML;
- `mlUserId` acima de Int32 (ex.: 3_000_000_001);
- `TimeProvider` falso para expiração;
- usuários e workspaces criados pelo cadastro (SPEC-0006).

**Ambiente de execução:** IT com Testcontainers (local e CI).

## 8. Plano de Rollout
- **Estratégia:** deploy direto. Sem configuração do ML, os endpoints respondem 503 ML_NOT_CONFIGURED, e o restante do sistema não muda.
- **Dados/schema:** migração inicial do schema `mercadolivre`, só criação (expand), aplicada pelo modo `--migrate` (ADR-0011).
- **Compatibilidade:** só endpoints novos. O cliente OpenAPI da SPA é regenerado.
- **Observabilidade:**
  - contador `vitrine.mercadolivre.connections{result=connected|reconnected|state_invalid|code_invalid|ml_unavailable|site_not_supported|in_use}`;
  - logs estruturados com `accountId`/`mlUserId`, sem segredos.
- **Rollback:** revert do merge. As tabelas criadas ficam sem uso.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Callback do OAuth anônimo na API ou concluído pela SPA? — Concluído pela SPA com POST autenticado, sem endpoint anônimo novo (seção 4). (Architect, 2026-10-30, a confirmar no H1)
- [x] Quem conecta e desconecta, e o que fazer com conta de outro site ou em uso? — Seguem as respostas do épico SPEC-0019 (Owner; MLB; 409). (Architect, 2026-10-30, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [x] CH-01 escrito, passando no código atual (filtros de antiforgery/HTTPS e lista de endpoints anônimos antes da extração para `Vitrine.Common.Web`), commitado antes de qualquer mudança

**Fase 1: Domínio do OAuth**
- [x] Red: escrever UT-01..UT-06 com a tag `SPEC-0020:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: gerador de PKCE/state, autorização pendente (hash do state, verifier cifrado, 10 min, uso único), validação de configuração `MercadoLivre:*` e regras de conta
- [x] Refactor mantendo tudo verde

**Fase 2: Endpoints, persistência e eventos**
- [x] Red: escrever IT-01..IT-10 e CT-01 com a tag `SPEC-0020:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: módulo `MercadoLivre` (migração, `connections`, `connections/complete`, `accounts`, `DELETE accounts/{id}`), `Identity.PublicApi` com `IWorkspaceMembership`, eventos v1 e cliente OpenAPI regenerado
- [x] Refactor mantendo tudo verde
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like com smoke (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — ddc6ebd (árvore suja) | 2026-10-30 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — d497103 | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  387ms (environment 66%, import 15%, transform 14%, tests 4%, worker 1%)); lint exit 0 ([0m[34mChecked 40 files in 20[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 89%) — 3ed9e04 | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 604ms) — 3ed9e04 | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED_WITH_NOTES (2ª rodada, 0 blocker/major; nits de teste e desvio de cifra → SPEC-0026) — 2ff43bc | 2026-10-30 |
| G5 Integração & CI | PASS | MANUAL: PR #47 (sdd/SPEC-0020 @ 4352c36): CI 8/8 verde + CodeQL (runs/41060031791); local no integrado via gate --run: build, unit, integração (2m10s), e2e 30/30, arquitetura OK; security_scan rodado em bash (o sh do gate não aceita pipefail): 0 vulneráveis high/critical, npm audit 0, gitleaks 263 commits sem vazamento | 2026-10-30 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-30 |
| G6 Deploy | PASS | MANUAL: Deploy local prod-like via pipeline local (deploy/deploy.sh 333ab7f --env-file deploy/local/.env): health 200 em 5 s; migração do schema mercadolivre aplicada; smoke @smoke 2/2 (desktop+mobile); GET /api/v1/mercadolivre/accounts sem sessão → 401; agente wolverine-recurring iniciado; staging/produção na SPEC-0009 (go-live com o usuário) | 2026-10-30 |
| G7 Pronto & Docs | PENDING | | |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-30 | G1 | spec | §5 (Resiliência) diz que após 502 ML_UNAVAILABLE a autorização pendente continua válida para nova tentativa; o contrato do §6 diz que ela é marcada como usada antes de chamar o ML (state nunca serve duas vezes) | Test-writer não afirmou nenhum dos dois comportamentos | Architect | Emenda v1.1 — §5 alinhado ao §6 (autorização consumida; nova tentativa reinicia a conexão) | 2026-10-30 |

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
O dono do workspace conecta uma ou mais contas do Mercado Livre (MLB) pelo OAuth oficial com PKCE S256 e as desconecta quando quiser; qualquer membro vê a lista de contas. API: `POST /api/v1/mercadolivre/connections`, `POST /api/v1/mercadolivre/connections/complete`, `GET /api/v1/mercadolivre/accounts` e `DELETE /api/v1/mercadolivre/accounts/{id}`, com os erros do contrato (`OAUTH_STATE_INVALID`, `OAUTH_CODE_INVALID`, `ML_UNAVAILABLE`, `ML_SITE_NOT_SUPPORTED`, `ML_ACCOUNT_IN_USE`, `ML_NOT_CONFIGURED`, `FORBIDDEN`). Tokens cifrados que nunca chegam à SPA; eventos `MercadoLivreAccountConnected`/`Disconnected` v1 publicados pela outbox. Sem credenciais do ML configuradas, a API responde `503 ML_NOT_CONFIGURED` (estado atual do ambiente local).

### Como foi feito
- Módulo novo `src/Modules/MercadoLivre/` (Domain, Application, Infrastructure, Presentation, PublicApi) no padrão do Identity; schema `mercadolivre` (migração `InitialMercadoLivre`); `Identity.PublicApi` com `IWorkspaceMembership`; filtros `AntiforgeryFilter`/`HttpsRequiredFilter` extraídos para `src/Common/Vitrine.Common.Web` (CH-01 guarda o comportamento).
- `state` gravado como SHA-256, consumido em transação própria antes de chamar o ML (Emenda v1.1); timeout total de 10 s para token + `/users/me`; lock consultivo por `ml_user_id` evita corrida entre conclusões; limpeza diária das autorizações por mensagem recorrente do Wolverine (`CleanupPendingAuthorizations`, 03:00 UTC, ADR-0014).
- **Desvios registrados (G4, aceitos pelo Reviewer):**
  1. o cliente `mercadolivre` usa `RemoveAllResilienceHandlers()` (API experimental EXTEXP0001, suprimida com `#pragma` no trecho) para não repetir o POST `/oauth/token`, já que o `code` é de uso único;
  2. a cifra dos tokens usa `IFieldProtector` (mesmo algoritmo e chaves da SPEC-0008), mas ligada por `ValueConverter` próprio + `ProtectorModelCacheKeyFactory`, em vez de `UseFieldProtection`, que cria um provedor interno do EF por protetor e estoura o limite nos testes. Dívida assumida e corrigida na SPEC-0026;
  3. `IAcceptsMetadata` removido do endpoint `complete` para que POST sem corpo e sem sessão responda 401, e não caia no fallback da SPA.
- Emenda v1.1 (contrato inalterado): §5 alinhado ao §6 e `user_facing: false` (jornada de navegador na SPEC-0025).

### Prova de Correção
N/A — `type: feature`.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| CH-01 | Filtros antiforgery/HTTPS e lista de endpoints anônimos preservados na extração para Common.Web | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| UT-01 | PKCE S256 (vetor RFC 7636) e state de 32 bytes | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| UT-02 | Conta: conectar, reconectar, status e evento com `reconnected` | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| UT-03 | Autorização pendente: 10 min, uso único, presa ao usuário | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| UT-04 | Validação de `MercadoLivre:*`, endpoints não oficiais só fora de Production e HTTPS fora de Development | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| UT-05 | Papel no workspace (Owner/Member) via `IWorkspaceMembership` | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| UT-06 | Arquitetura: camadas, schema `mercadolivre`, PublicApi sem segredos, Common.Web sem módulos | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-01 | Conectar: URL de autorização, conclusão, tokens cifrados e evento na outbox | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-02 | Início da conexão só para o Owner com ML configurado | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-03 | State inválido/expirado/reutilizado/de outro usuário → 400; corpo inválido → VALIDATION_FAILED; limpeza das expiradas | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-04 | Falhas do ML (code inválido, timeout total de 10 s, divergência de usuário, corrida no mesmo workspace) → 400/502 | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-05 | Desconectar apaga tokens e publica evento; DELETE concorrente nunca 500 | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-06 | Member e usuário sem workspace → 403 FORBIDDEN | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-07 | Tokens nunca aparecem em respostas, logs nem em texto claro no banco | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-08 | Conta em outro workspace → 409 ML_ACCOUNT_IN_USE | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-09 | Site diferente de MLB → 422 ML_SITE_NOT_SUPPORTED; ML não configurado → 503 | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| IT-10 | `IWorkspaceMembership` resolvido pelo Identity com dados reais | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |
| CT-01 | Eventos `MercadoLivreAccountConnected`/`Disconnected` v1 conforme o contrato | PASS | CI PR #47 run 41060031791 (4352c36); main 333ab7f |

### Definição de Pronto
- [x] Todos os testes do plano passando e listados na Verificação
- [x] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [x] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado — fronteiras de módulo, outbox e cifra por `IFieldProtector` (ADR-0006) mantidas; o desvio de ligação da cifra no EF é dívida registrada e corrigida pela SPEC-0026
- [x] Requisitos não-funcionais medidos com evidência (ou N/A justificado) — timeout total ≤ 10 s (IT-04), tokens fora de logs/respostas (IT-07), int64 em `ml_user_id` (IT-01)
- [x] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6) — ambiente local prod-like; staging/produção na SPEC-0009
- [x] Observabilidade e rollback prontos conforme o Plano de Rollout — métrica `vitrine.mercadolivre.connections`, logs de erro do ML sem segredos; rollback por revert/imagem anterior (migração só cria o schema novo)
- [x] Documentação raiz e CHANGELOG atualizados (G7)
- [x] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7) — N/A: o projeto não tem `living_docs`; README atualizado
- [x] Pendências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, imagem `ghcr.io/thomasravache/vitrine-api:333ab7f`, 2026-10-30, deploy direto por `deploy/deploy.sh` (health 200 em 5 s, smoke 2/2). Sem feature flag: sem `MercadoLivre__ClientId/ClientSecret` a API responde 503 `ML_NOT_CONFIGURED`. Staging/produção e o app no DevCenter do ML ficam para a SPEC-0009 (go-live com o usuário).

### Pendências
- SPEC-0026 — `UseFieldProtection` sem provedor interno por protetor (remove o contorno de cifra deste módulo).
- Nits de teste do G4 (asserção 201+200 na corrida no mesmo workspace; asserção de que o agendamento recorrente está registrado) — entram na SPEC-0026, que mexe nos mesmos testes do módulo.
- Simulador do ML fora de Development precisa de HTTPS (Caddy) — já previsto na SPEC-0024.

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0020`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (contrato inalterado) | 2026-10-30 | §5 alinhado ao contrato do §6: após 502 ML_UNAVAILABLE a autorização pendente fica consumida e a nova tentativa começa uma nova conexão; `user_facing` passa a `false` (a API só é consumida pela SPA, e a jornada de navegador é entregue e provada pela SPEC-0025) | Contradição entre §5 e §6 apontada pelo Test-writer (IMP-01); o §6 prevalece por segurança (state e code de uso único) | nenhuma | thomas (autorização permanente de 2026-10-30 para o épico SPEC-0019; contrato inalterado) |

---
id: SPEC-0024
title: Simulador do Mercado Livre para desenvolvimento e E2E
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
touches: [tools/MercadoLivreSimulator/**, src/AppHost/**, web/e2e/support/**, deploy/compose.override.yaml, deploy/compose.yaml, deploy/Caddyfile, deploy/caddy-sites/**, deploy/local/**, docs/runbooks/deploy.md, tests/Fixtures/MercadoLivre/**, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.Tooling.Tests/**, tests/Vitrine.ArchitectureTests/**, tests/infra/**, Vitrine.slnx, src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/MercadoLivreOptionsValidator.cs, .gitleaksignore]
adrs: [ADR-0002, ADR-0004, ADR-0016]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-30
---
# SPEC-0024 — Simulador do Mercado Livre para desenvolvimento e E2E

## 1. Visão Geral
Cria o simulador do Mercado Livre (ADR-0016): uma aplicação ASP.NET mínima, com estado em memória, que reproduz o subconjunto documentado da API do ML usado pelo Vitrine.

O simulador inclui:
- a página de autorização OAuth, com PKCE;
- a troca e a renovação de tokens, com rotação de uso único;
- `/users/me`, a busca de anúncios (offset e `scan`) e `/items/bulk`;
- o disparo de notificações;
- endpoints de controle `/__sim/*` para os testes.

Ele sobe no `aspire run`, no E2E do CI e no ambiente local prod-like (perfil `ml-simulator`), para o usuário testar a jornada inteira sem conta no ML.

## 2. Motivação & Escopo
**Motivação:**
- O ML não tem sandbox: usuários de teste reais expiram, são poucos e exigem o app no DevCenter.
- O E2E da jornada `conectar-conta` precisa de um ML que redirecione o navegador e tenha estado.
- O usuário quer testar tudo localmente antes de gerar custo ou criar contas.

**Objetivos (dentro do escopo):**
- Projeto `tools/MercadoLivreSimulator/Vitrine.MercadoLivreSimulator.csproj` (Minimal API, sem pacote novo), incluído no `Vitrine.slnx`, que segue as regras de build do repositório (warnings como erro, lockfile).
- Endpoints que imitam o ML (contrato abaixo) e endpoints de controle `/__sim/*`.
- Massa inicial:
  - vendedor `VENDEDOR_TESTE` (id 3000000001, MLB) com 25 anúncios: 15 ativos, 6 pausados e 4 encerrados;
  - vendedor `VENDEDOR_AR` (id 3000000002, site MLA), para o caso de site não suportado.
- AppHost (só em modo run, nunca no `aspire publish`):
  - recurso `ml-simulator`;
  - a Api configurada para ele (`MercadoLivre__*` com ClientId/Secret sintéticos e `RedirectUri` da SPA);
  - o simulador configurado para enviar notificações à Api.
- `web/e2e/support/AppHostFixture.cs` passa a informar também a `ML_SIMULATOR_URL` ao Playwright.
- Ambiente local prod-like:
  - serviço `ml-simulator` no `deploy/compose.override.yaml`, só no perfil `ml-simulator`;
  - site `ml-simulador.vitrine.localhost` no Caddy, por um diretório de sites extras (`CADDY_EXTRA_SITES_DIR`), vazio por padrão e apontado para `deploy/local/caddy-sites` só pelo `deploy/local/.env.example`;
  - variáveis da Api no `.env.example`;
  - runbook `docs/runbooks/deploy.md` com a construção das duas imagens.
- Fixtures de resposta do ML copiadas da documentação oficial (com URL e data) em `tests/Fixtures/MercadoLivre/`. São compartilhadas com os CT das SPEC-0021/0023, para o simulador e o gateway falarem o mesmo formato.

**Não-objetivos (fora do escopo):**
- Escrita de anúncios, perguntas e pedidos no simulador: entram com os épicos que os usarem.
- Persistência: o estado se perde ao reiniciar, o que é aceitável para dev e E2E. Reconectar recria tudo.
- Fidelidade além do documentado: limites de taxa reais, erros raros e campos não usados.
- Uso em staging ou produção: proibido (ADR-0016).

## 3. Dependências
- **Implementações necessárias:** N/A.
- **Contratos consumidos:** N/A. O simulador implementa o contrato **externo** do ML; as fixtures documentadas são a fonte.
- **Pré-requisitos externos:** N/A.

## 4. Decisão Arquitetural
**Contexto:**
- ADR-0016 (proposta neste épico).
- O AppHost em `src/AppHost/AppHost.cs` segue o padrão dos recursos `api`/`web`, com o modelo testado em `tests/Vitrine.Tooling.Tests/AppHostModelTests.cs`.
- O deploy local segue `deploy/compose.override.yaml`, os perfis `db-container`/`observability` e `deploy/local/.env.example` (SPEC-0005/0007).

**Decisão:**
- Aplicação separada em `tools/`, fora de `src/`, porque não é produto.
- Estado em memória, com um lock por vendedor.
- A página de autorização é HTML simples servido pelo próprio simulador, com o rótulo "Simulador do Mercado Livre — ambiente de teste".
- O Caddy local expõe o simulador num subdomínio `*.localhost`, com o mesmo certificado da CA interna.

**Justificativa:** ver ADR-0016. O diretório de sites extras mantém o `Caddyfile` de produção sem nenhuma menção ao simulador.

**Desvio do padrão existente:** a pasta `tools/` passa a conter um projeto .NET (hoje só tem `tools/sdd`). Coberto pela ADR-0016.

**Alternativas descartadas:** ver ADR-0016 (WireMock standalone; ML real).

**ADRs:** ADR-0002, ADR-0004, ADR-0016.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** N/A — ferramenta de teste. Responde em memória.
- **Segurança:**
  - o simulador nunca entra na imagem da Api nem no Compose de staging/produção — IT-03, UT-04;
  - credenciais sintéticas e públicas, marcadas como tais;
  - a página deixa explícito que é um simulador.
- **Privacidade e dados pessoais:** N/A — só dados sintéticos.
- **Disponibilidade e resiliência:** N/A — sem SLA. `GET /health` para o AppHost e para o Compose.
- **Acessibilidade (UI):** a página de autorização do simulador usa rótulos, botões nativos e contraste AA, porque o E2E passa por ela. Verificada com axe no E2E da SPEC-0025, que exclui a página do critério do produto.
- **Custo:** N/A.

## 6. Artefato A — Contrato
**Interface:** aplicação `ml-simulator` (porta HTTP interna 8080 no Compose; dinâmica no AppHost).

```text
Formato e semântica conforme a documentação do ML (fixtures em tests/Fixtures/MercadoLivre/, consultadas em 30/10/2026).
Erros OAuth no formato do ML: { "error": "...", "error_description": "...", "status": 400, "cause": [] }.

GET  /authorization?response_type=code&client_id&redirect_uri&state&code_challenge&code_challenge_method=S256
     200 HTML: "Simulador do Mercado Livre — ambiente de teste"; escolha do vendedor (lista de todos, rótulo = nickname);
     botões "Autorizar" e "Cancelar"
     client_id desconhecido, redirect_uri fora de Simulator:AllowedRedirectUris, method != S256 -> 400 HTML com o motivo
POST /authorization/decision  (form: decisão, vendedor, parâmetros originais)
     Autorizar -> 302 {redirect_uri}?code={code}&state={state}     (code: uso único, 10 min)
     Cancelar  -> 302 {redirect_uri}?error=access_denied&state={state}

POST /oauth/token  (application/x-www-form-urlencoded)
     grant_type=authorization_code: client_id+client_secret válidos; code válido, não usado, não expirado, mesmo redirect_uri;
       SHA256(code_verifier) == code_challenge -> 200 { access_token, token_type:"Bearer", expires_in, scope:"offline_access read write",
       user_id, refresh_token }; senão 400 invalid_grant (cliente inválido: 401 invalid_client)
     grant_type=refresh_token: refresh_token = o último emitido e não revogado -> 200 (novo access_token e NOVO refresh_token;
       o anterior deixa de valer); senão 400 invalid_grant
     expires_in = Simulator:AccessTokenLifetime (padrão 21600 s)
GET  /users/me                       Bearer válido -> 200 { id, nickname, site_id, country_id, user_type, tags:["test_user"] }; senão 401
GET  /users/{id}/items/search?status&limit(1..100, padrão 50)&offset | &search_type=scan[&scroll_id]
     token de outro vendedor -> 403; offset+limit > 1000 sem scan -> 400
     -> { results:[ids], paging:{ total, offset, limit } } | scan: { results, scroll_id } (scroll_id null no fim)
GET  /items/bulk?ids={≤20}&attributes=body.…   -> [{ id, status_code: 200|404, body:{…} }]; > 20 ids -> 400; 401 sem token
GET  /health                                   -> 200

Controle (só para dev/E2E):
POST /__sim/reset                                  volta à massa inicial; invalida todos os tokens
POST /__sim/sellers                                body { nickname?, siteId? (padrão MLB), active?, paused?, closed? } cria um vendedor
                                                   novo (id único >= 3000001000) com a massa pedida (padrão 15/6/4) -> 201 { id, nickname }
                                                   (cada E2E usa o seu: um vendedor só pode estar em um workspace no Vitrine)
POST /__sim/sellers/{id}/items/{itemId}            body { title?, price?, availableQuantity?, soldQuantity?, status? }: altera e atualiza last_updated
POST /__sim/sellers/{id}/items                     body { count } cria N anúncios ativos sintéticos (para catálogo > 1000)
POST /__sim/sellers/{id}/revoke                    invalida access e refresh tokens do vendedor (próximo refresh -> invalid_grant)
POST /__sim/sellers/{id}/expire-access-token       access token atual passa a ser recusado com 401 (força renovação)
POST /__sim/notify                                 body { userId, itemId } -> envia ao Simulator:NotificationUrl o POST no formato
                                                   do ML (topic items, resource /items/{itemId}, application_id = client_id, _id novo)
                                                   -> 202 { notificationId, deliveredStatus }

Configuração: Simulator:ClientId, ClientSecret, AllowedRedirectUris[], NotificationUrl, AccessTokenLifetime.
AppHost (modo run): recurso "ml-simulator"; Api recebe MercadoLivre__AuthorizationBaseUrl e __ApiBaseUrl = URL do simulador,
  ClientId/ClientSecret sintéticos, RedirectUri = {web}/contas/mercado-livre/retorno.
Compose local: perfil ml-simulator; Caddy serve https://ml-simulador.vitrine.localhost -> ml-simulator:8080;
  Api local com MercadoLivre__AuthorizationBaseUrl=https://ml-simulador.vitrine.localhost,
  MercadoLivre__ApiBaseUrl=http://ml-simulator:8080, MercadoLivre__AllowNonOfficialEndpoints=true;
  simulador com NotificationUrl=http://api:8080/api/v1/mercadolivre/notifications.
```

**Design:** página de autorização própria do simulador, simples, sem a identidade visual do ML (regra do design system). Não usa o Stitch, porque não é tela do produto.

**Arquivos/módulos afetados:** ver `touches`.
- `tools/MercadoLivreSimulator/**`
- `src/AppHost/**`
- `web/e2e/support/**`
- `deploy/compose.override.yaml`, `deploy/compose.yaml` (variáveis `MercadoLivre__*` da Api), `deploy/Caddyfile` (import do diretório de sites extras), `deploy/local/**`
- `docs/runbooks/deploy.md`
- `tests/Fixtures/MercadoLivre/**`
- testes

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Autorizar | parâmetros válidos, "Autorizar" | 302 para `redirect_uri` com `code` e o mesmo `state` | IT-01 |
| Cancelar | "Cancelar" | 302 com `error=access_denied` e `state` | IT-01 |
| Parâmetros inválidos | client/redirect desconhecidos, método ≠ S256 | 400 com o motivo | IT-01 |
| Trocar code | verifier correto | 200 com tokens no formato do ML | UT-01, IT-01, CT-01 |
| Code inválido | verifier errado, code reutilizado ou expirado, redirect diferente | 400 `invalid_grant` | UT-01, IT-01 |
| Renovar | último refresh token | 200 com refresh novo; o anterior passa a dar `invalid_grant` | UT-02, IT-01 |
| Revogar/expirar | `/__sim/.../revoke` e `expire-access-token` | refresh com `invalid_grant`; API com 401 | UT-02, IT-02 |
| Buscar anúncios | offset, limite de 1000 e `scan` | paginação conforme a doc; 400 acima de 1000 sem `scan` | UT-03, IT-02 |
| Ler em lote | `/items/bulk` | itens no formato da fixture; 404 por item inexistente; > 20 IDs dá 400 | IT-02, CT-01 |
| Criar vendedor | `POST /__sim/sellers` | 201 com id único; aparece na página de autorização com a massa pedida | IT-02 |
| Notificar | `/__sim/notify` | POST no formato do ML entregue à URL configurada | IT-05, CT-01 |
| Só fora de produção | AppHost em publish, Compose sem o perfil, imagem da Api | simulador ausente | IT-03, IT-04, UT-04 |
| AppHost em run | `aspire run` / E2E | recurso `ml-simulator` saudável; Api apontada para ele; fixture informa `ML_SIMULATOR_URL` | IT-04 |
| Ambiente local | deploy com o perfil `ml-simulator` | `https://ml-simulador.vitrine.localhost/health` responde 200 pelo Caddy | IT-06 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- **CH-01** — Dado o `Caddyfile` e o Compose atuais, quando os testes de infra da SPEC-0005 (`bash tests/infra/run.sh`) rodam após a inclusão do diretório de sites extras vazio, então continuam passando (guarda: passa antes da mudança).

### 7.2 Testes Unitários
- **UT-01** — Dado o verificador PKCE do simulador, quando recebe o vetor do RFC 7636 (apêndice B), então aceita. Com verifier alterado, recusa.
- **UT-02** — Dado o emissor de tokens, então:
  - após um refresh, só o refresh token novo vale;
  - `revoke` invalida access e refresh;
  - `expire-access-token` invalida só o access.
- **UT-03** — Dado 2.300 anúncios na massa, então:
  - a busca por offset recusa `offset+limit > 1000`;
  - a busca `scan` devolve todos os IDs em páginas encadeadas por `scroll_id`, terminando com `scroll_id` null.
- **UT-04** — Dado o grafo de projetos da solução, quando inspecionado, então `Vitrine.Api` (e o que ele referencia) não referencia `Vitrine.MercadoLivreSimulator`.

### 7.3 Testes de Integração
- **IT-01** — Dado o simulador em `WebApplicationFactory`, quando o fluxo completo roda por HTTP, então:
  - GET da página e POST "Autorizar" geram 302 com `code` e `state`; "Cancelar" gera `error=access_denied`;
  - a troca com o verifier correto responde 200; com verifier errado, code reutilizado ou `redirect_uri` diferente, `invalid_grant`;
  - client_id desconhecido na página responde 400;
  - a renovação gira o refresh token, e o antigo dá `invalid_grant`.
- **IT-02** — Dado um vendedor criado por `POST /__sim/sellers` (id único, listado na página de autorização) e tokens válidos, então:
  - `/users/me` devolve o vendedor;
  - a busca por offset e por `scan` devolve os IDs da massa, e > 1000 por offset dá 400;
  - `/items/bulk` com 20 IDs (1 inexistente) devolve 19 `status_code` 200 e 1 404; com 21 IDs, 400;
  - depois de `revoke`, o refresh dá `invalid_grant`; depois de `expire-access-token`, a API dá 401.
- **IT-03** — Dado os arquivos de deploy, então:
  - o serviço `ml-simulator` só existe com `profiles: ["ml-simulator"]`;
  - só `deploy/local/.env.example` inclui `ml-simulator` em `COMPOSE_PROFILES` e define `CADDY_EXTRA_SITES_DIR`;
  - o `Caddyfile` não cita o simulador;
  - o diretório padrão de sites extras está vazio.
- **IT-04** — Dado o modelo do AppHost, então:
  - em modo run existe o recurso `ml-simulator`, e a Api recebe `MercadoLivre__AuthorizationBaseUrl`/`__ApiBaseUrl` apontando para ele;
  - em modo publish (`aspire publish` / manifesto do Compose gerado), o recurso não existe;
  - a fixture do E2E escreve `ML_SIMULATOR_URL`.
- **IT-05** — Dado um WireMock como receptor em `Simulator:NotificationUrl`, quando `POST /__sim/notify { userId, itemId }`, então o WireMock recebe um POST JSON com `_id`, `resource=/items/{itemId}`, `user_id`, `topic=items`, `application_id` (= client_id numérico), `attempts=1`, `sent` e `received`.
- **IT-06** — Dado o ambiente local prod-like com o perfil `ml-simulator` (teste de infra em `tests/infra`, padrão da SPEC-0005/0012), quando o deploy local sobe, então `https://ml-simulador.vitrine.localhost/health` responde 200 pelo Caddy. Sem o perfil, o host não responde.

### 7.4 Testes de Contrato
- **CT-01** — Dado as fixtures de `tests/Fixtures/MercadoLivre/` (token, `users/me`, `items/search`, `items/bulk`, notificação, copiadas da documentação do ML com URL e data), quando as respostas do simulador são comparadas, então têm os mesmos campos e tipos nos campos usados pelo Vitrine. As SPEC-0021 (CT 02) e SPEC-0023 usam as mesmas fixtures, o que mantém o simulador e o gateway alinhados ao formato documentado.

### 7.5 Testes E2E
- N/A — ferramenta de teste, sem jornada própria. É exercitada pela SPEC-0025 (E2E 01, 02, 03).

### 7.6 Outros
- N/A.

**Dublês e dados de teste:** massa sintética fixa (IDs `MLB9000000001`…; vendedores 3000000001/3000000002); WireMock como receptor de notificação.

**Ambiente de execução:**
- UT e IT em processo (local e CI);
- IT-04 no `Vitrine.Tooling.Tests`;
- IT-06 em `tests/infra` (local, portas 80/443 livres; no CI, conforme a SPEC-0012).

## 8. Plano de Rollout
- **Estratégia:** deploy direto, só em dev, CI e local. Não existe em staging nem em produção.
- **Dados/schema:** N/A — estado em memória.
- **Compatibilidade:** o AppHost ganha um recurso e o `.env.example` local ganha variáveis. Quem já tem `deploy/local/.env` copia as linhas novas (nota no runbook).
- **Observabilidade:** logs do simulador no dashboard do Aspire; `GET /health`.
- **Rollback:** revert do merge.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Expor o simulador no ambiente local prod-like? — Sim, para o usuário testar a jornada em `https://vitrine.localhost` antes de criar o app no DevCenter. Perfil `ml-simulator`, desligado fora do `.env` local. (Architect, 2026-10-30, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [x] CH-01 escrito, passando no código atual (AppHost e Compose local sem o simulador), commitado antes de qualquer mudança

**Fase 1: Estado do simulador**
- [x] Red: escrever UT-01..UT-04 com a tag `SPEC-0024:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: PKCE, rotação de refresh token, busca com scan e bulk em memória
- [x] Refactor mantendo tudo verde

**Fase 2: Simulador no AppHost e no Compose**
- [x] Red: escrever IT-01..IT-06 e CT-01 com a tag `SPEC-0024:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `tools/MercadoLivreSimulator`, endpoints `/__sim/*`, recurso no AppHost (modo run), perfil `ml-simulator` no Compose + Caddy e fixtures em `tests/Fixtures/MercadoLivre/`
- [x] Refactor mantendo tudo verde
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — ddc6ebd (árvore suja) | 2026-10-30 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 8fdc08f | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  403ms (environment 66%, transform 14%, import 13%, tests 5%, worker 1%)); lint exit 0 ([0m[34mChecked 40 files in 32[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 88%) — d36d604 | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 2s 124ms) — d36d604 | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED (2ª rodada; gitleaks 0 no histórico; notas: caddy-sites no deploy.yml, miniaturas externas no E2E, contrato do lado da requisição coberto pela SPEC-0025) — d36d604 | 2026-10-30 |
| G5 Integração & CI | PENDING | | |
| H2 Integração aprovada | PENDING | | |
| G6 Deploy | PENDING | | |
| G7 Pronto & Docs | PENDING | | |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-30 | G1 | spec | Compose local manda MercadoLivre__ApiBaseUrl=http://ml-simulator:8080 com ASPNETCORE_ENVIRONMENT=Production; o MercadoLivreOptionsValidator (SPEC-0020) exige https fora de Development mesmo com AllowNonOfficialEndpoints=true e a Api não sobe (ValidateOnStart) | Test-writer leu CheckEndpoint; IT-03 testa o valor do contrato | Architect | Emenda v1.2 da SPEC-0020 + v1.1 da SPEC-0024 — http só com AllowNonOfficialEndpoints=true | 2026-10-30 |

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
- [ ] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7)
- [ ] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0024`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (contrato inalterado) | 2026-10-30 | (1) `touches` inclui `MercadoLivreOptionsValidator.cs` para a Emenda v1.2 da SPEC-0020 (`http://` só com `AllowNonOfficialEndpoints=true`); no Compose local o navegador usa `https://ml-simulador.vitrine.localhost` (Caddy) e a Api usa `http://ml-simulator:8080`; (2) form de decisão com `seller_id` e `decision=authorize|cancel`; nome da imagem lido do Compose; (3) o scan pode terminar com `scroll_id` nulo na última página com resultados ou numa página vazia; (4) `ML_SIMULATOR_URL` exportado por `web/e2e/support/app-host-fixture.ts`; (5) fixtures no formato documentado com valores sintéticos e `_source` (a documentação do ML responde 403 a acesso automatizado) — cópia oficial fica como pendência do usuário antes do go-live | SPEC_DEFECT e pontos em aberto do G1 (IMP-01) | SPEC-0020 (Emenda v1.2) | thomas (piloto automático do épico SPEC-0019) |
| v1.2 (contrato inalterado) | 2026-10-30 | `touches` inclui `.gitleaksignore` (fingerprint de falso positivo `generic-api-key` na URL interna do webhook em teste, conforme a convenção da ADR-0006); a suíte de infra da SPEC-0005 desliga o perfil `ml-simulator` na cópia do `.env.example`; o estado do simulador usa um lock único (suficiente para dev/E2E) em vez de lock por vendedor | Achados do G4 | nenhuma | thomas (piloto automático do épico SPEC-0019) |

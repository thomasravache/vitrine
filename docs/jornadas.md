# Guia de jornadas

<!-- Documento vivo: o que cada perfil faz no produto hoje, passo a passo, como reproduzir localmente e qual teste
prova cada passo. Uma seção por jornada de `critical_journeys` (docs/specs/sdd-config.yml). Atualizado no CLOSE (G7)
de cada entrega que muda uma jornada; `python3 tools/sdd/spec_graph.py living-docs` mostra a cobertura. -->

## Perfis

- **Dono do workspace (Owner)** — vendedor do Mercado Livre que cria a conta no Vitrine; conecta e desconecta contas
  do ML e vê os anúncios.
- **Membro (Member)** — pessoa do mesmo workspace; vê contas e anúncios e pode sincronizar, mas não conecta nem
  desconecta contas. (Ainda não há convite pela tela; o papel existe no modelo.)

## Mapa de telas e rotas

| Tela | Rota da SPA | API usada |
|---|---|---|
| Início | `/` | — |
| Cadastro | `/cadastro` | `GET /api/v1/auth/antiforgery`, `POST /api/v1/auth/register` |
| Entrar | `/entrar` | `POST /api/v1/auth/login` |
| Painel | `/painel` | `GET /api/v1/me`, `GET /api/v1/mercadolivre/accounts`, `GET /api/v1/listings/summary` |
| Contas | `/contas` | `GET /api/v1/mercadolivre/accounts`, `POST /api/v1/mercadolivre/connections`, `DELETE /api/v1/mercadolivre/accounts/{id}` |
| Retorno da autorização do ML | `/contas/mercado-livre/retorno` | `POST /api/v1/mercadolivre/connections/complete` |
| Anúncios | `/anuncios?status=&conta=&q=` | `GET /api/v1/listings`, `GET /api/v1/listings/summary`, `POST /api/v1/listings/sync` |
| Sair | botão "Sair" | `POST /api/v1/auth/logout` |

Fora da SPA: `POST /api/v1/mercadolivre/notifications` (webhook chamado pelo ML, anônimo, fora do OpenAPI) e os
health checks `GET /health/live` e `GET /health/ready`.

## Cadastro, entrada e painel [jornada: acesso]

- Perfil: qualquer vendedor (vira Owner do próprio workspace).
- Passo a passo:
  1. Em `/cadastro`, informa nome, e-mail e senha → conta e workspace criados, sessão aberta, vai para `/painel`.
  2. No `/painel` vazio, vê "Conecte sua conta do Mercado Livre".
  3. Clica em "Sair" → volta para `/entrar`.
  4. Em `/entrar`, informa e-mail e senha → volta ao `/painel`.
  - Limites: 10 tentativas por minuto por IP no cadastro e no login (e 10/min por e-mail no login) → 429.
- Como reproduzir localmente: `aspire run` (abre a SPA pelo AppHost) ou o ambiente local prod-like em
  `https://vitrine.localhost` (runbook `docs/runbooks/deploy.md`; do Mac, túnel SSH para a porta 443 da VPS). O
  cadastro está aberto em dev e no ambiente local (`FEATURES_SIGNUP_OPEN=true`).
- Provada por: SPEC-0006:E2E-01 (`web/e2e/acesso.spec.ts`, `@smoke`).

## Conectar conta do Mercado Livre e ver os anúncios [jornada: conectar-conta]

- Perfil: Owner (conecta/desconecta); Member vê e sincroniza.
- Passo a passo:
  1. No `/painel` ou em `/contas`, clica em "Conectar conta" → a SPA pede a URL de autorização e o navegador vai
     para o Mercado Livre (local: o simulador em `https://ml-simulador.vitrine.localhost`).
  2. No ML/simulador, escolhe o vendedor e autoriza → volta para `/contas/mercado-livre/retorno?code=…&state=…`;
     a tela mostra "Concluindo a conexão…", limpa a URL e conclui.
  3. Em `/contas`, vê "Conta {nickname} conectada. Estamos importando seus anúncios." e o selo "Conectada"; a
     contagem "Anúncios: N" sobe enquanto a importação roda.
  4. Em `/anuncios`, vê os anúncios (padrão: ativos), com busca por título ou código (`MLB…`), filtros
     Ativos/Pausados/Encerrados/Todos, seletor de conta (com 2+ contas) e "Carregar mais" de 20 em 20.
  5. "Sincronizar agora" atualiza na hora (no máximo a cada 5 min por conta → "Você sincronizou há pouco").
  6. Mudanças feitas no ML chegam sozinhas pela notificação; uma reconciliação diária recupera o que se perdeu.
  7. Se o ML recusar o acesso, a conta mostra "Reconectar necessário" e o botão "Reconectar".
  8. Em `/contas`, "Desconectar" pede confirmação e apaga a conta e os anúncios dela.
  - Cancelar no ML volta com "Você cancelou a autorização no Mercado Livre."; a mesma conta do ML em outro
    workspace dá "já está conectada em outro workspace".
- Como reproduzir localmente: ambiente local prod-like com o perfil `ml-simulator` (runbook
  `docs/runbooks/deploy.md`), entre em `https://vitrine.localhost`, crie uma conta, clique em "Conectar conta" e
  autorize **VENDEDOR_TESTE** (MLB: 15 ativos, 6 pausados, 4 encerrados). No `aspire run`, o simulador sobe junto.
- Provada por: SPEC-0025:E2E-01 (`@smoke`), SPEC-0025:E2E-02 (reconectar), SPEC-0025:E2E-03 (desconectar),
  SPEC-0025:E2E-04 (cancelar), SPEC-0025:E2E-05 (sincronizar e limite) — `web/e2e/conectar-conta.spec.ts`; regras
  de API em SPEC-0020/0021/0022/0023.

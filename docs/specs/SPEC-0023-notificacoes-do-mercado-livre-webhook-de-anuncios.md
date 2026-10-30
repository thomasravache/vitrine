---
id: SPEC-0023
title: Notificações do Mercado Livre (webhook de anúncios)
tier: full
type: feature
user_facing: false
status: in-progress
created: 2026-10-30
parent: SPEC-0019
depends_on: []
consumes_contract: [SPEC-0020@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [src/Modules/MercadoLivre/**, src/Api/Security/**, src/Api/Program.cs, tests/Vitrine.UnitTests/**, tests/Vitrine.IntegrationTests/**, tests/Vitrine.ArchitectureTests/**, deploy/compose.yaml, deploy/local/.env.example]
adrs: [ADR-0001, ADR-0010, ADR-0014]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-30
---
# SPEC-0023 — Notificações do Mercado Livre (webhook de anúncios)

## 1. Visão Geral
Recebe as notificações do Mercado Livre (webhook) no endpoint `POST /api/v1/mercadolivre/notifications`.

O endpoint:
- grava cada notificação no inbox durável;
- responde 200 bem abaixo dos 500 ms exigidos pelo ML;
- processa depois. Para o tópico `items`, publica `MercadoLivreItemChanged`, que o módulo Listings usa para reler o anúncio.

É a base que o épico de perguntas reutiliza (tópico `questions`).

## 2. Motivação & Escopo
**Motivação:** sem notificação, o Vitrine só veria mudanças feitas no ML na reconciliação diária. O ML exige HTTP 200 em até 500 ms; se não, retenta por 1 h (cerca de 8 tentativas) e pode desativar o tópico. A ADR-0014 manda gravar no inbox e responder imediatamente.

**Objetivos (dentro do escopo):**
- Endpoint anônimo `POST /api/v1/mercadolivre/notifications`, fora do documento OpenAPI (não é API da SPA), com limite de corpo de 16 KB e rate limit por IP.
- Validação do payload. Descarte silencioso, com 200, de `application_id` diferente do app e de tópicos não tratados.
- Gravação no inbox `mercadolivre.notifications` com deduplicação pelo `_id` da notificação, e mensagem durável `ProcessMercadoLivreNotification` na mesma transação (outbox).
- Processamento: localiza a conta pelo `user_id`. Para o tópico `items` com `resource` `/items/{MLB…}`, publica o evento `MercadoLivreItemChanged` v1.
- Inclusão do endpoint na lista `AnonymousEndpoints` do teste de autorização por padrão (SPEC-0008). É a única exceção nova, por emenda da SPEC-0008.
- Limpeza do inbox: linhas processadas há mais de 7 dias são apagadas pelo job diário do módulo.
- Métricas:
  - `vitrine.mercadolivre.notifications{topic,result=accepted|duplicate|ignored_app|ignored_topic|unknown_user|invalid}`;
  - duração da requisição, pela instrumentação HTTP existente.

**Não-objetivos (fora do escopo):**
- Tópicos além de `items`: `questions` vem no épico de perguntas; `orders_v2` e outros, quando houver uso.
- `GET /missed_feeds`: a reconciliação diária da SPEC-0022 recupera as perdas. O uso do missed_feeds (janela de 2 dias, `site_id` obrigatório para `items`) fica para quando houver tópico com SLA (perguntas).
- Filtro de IP de origem do ML no Caddy: a lista de cerca de 90 IPs publicada pelo ML entra como defesa opcional no go-live (SPEC-0009), porque muda sem aviso.
- Configuração do webhook no DevCenter: ação do usuário no go-live (URL pública HTTPS).

## 3. Dependências
- **Implementações necessárias:** N/A.
- **Contratos consumidos:** SPEC-0020@1, que fornece a tabela `accounts` (`ml_user_id` → conta/workspace), a configuração `MercadoLivre:ClientId` (o `application_id` esperado) e o padrão de eventos do módulo.
- **Pré-requisitos externos:** N/A para dev, CI e local (as notificações são simuladas por POST nos testes e pelo simulador da SPEC-0024). Para receber de verdade: URL pública HTTPS cadastrada no DevCenter, no go-live.

## 4. Decisão Arquitetural
**Contexto:**
- Segue o módulo da SPEC-0020 e a ADR-0014: webhook persistido no inbox e respondido com 200 imediatamente, com processamento em handler durável.
- O padrão de mensagem durável é o de `IdentityModule` (`LocalQueueFor<…>().UseDurableInbox()`).
- O padrão de rate limit é `UseAuthRateLimiting` (SPEC-0008, `src/Api/Security`).

**Decisão:**
- O endpoint faz só validação barata e um INSERT com a mensagem no outbox. Toda leitura no ML acontece depois, já que o payload não é confiável: o ML não documenta assinatura.
- A deduplicação é pelo `_id` da notificação (único por notificação; `ON CONFLICT DO NOTHING`).

**Justificativa:**
- Cumpre os 500 ms com folga.
- Um payload forjado só provoca uma releitura legítima na API do ML, com o token da conta, e nunca grava dados vindos do corpo.

**Desvio do padrão existente:**
- **Endpoint anônimo novo:** exceção à autorização por padrão da SPEC-0008, registrada na lista `AnonymousEndpoints` (emenda da SPEC-0008 apresentada no H1 deste épico).
- **Deduplicação:** a ADR-0010 fala em deduplicar pelo par (tópico, recurso, tentativa). O `_id` identifica a notificação, e uma tentativa repetida com o mesmo `_id` é descartada. Se o ML usar `_id` novo por tentativa (não verificado), o efeito é só uma releitura extra e idempotente. Nenhuma mudança na ADR: é a mesma garantia (processamento idempotente) com uma chave mais precisa.

**Alternativas descartadas:**
- Processar a notificação na própria requisição: arrisca os 500 ms e a desativação do tópico.
- Deduplicar por (tópico, recurso): descartaria mudanças reais seguidas no mesmo anúncio.
- Exigir um segredo na URL do webhook (`?key=`): o ML não documenta isso, e o segredo viveria em URL e logs de proxy. O ganho é pequeno, porque o payload já não é confiável.

**ADRs:** ADR-0001, ADR-0010, ADR-0014.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** p95 < 100 ms para o 200 do webhook (limite do ML: 500 ms), medido no IT-05 com 50 POSTs seguidos contra Postgres real.
- **Segurança:**
  - anônimo e sem antiforgery: chamado por servidor, sem cookie;
  - corpo ≤ 16 KB (413);
  - rate limit de 600/min por IP (`RateLimiting:MercadoLivreNotificationsPerIp:PermitLimit`, no padrão de `AuthPerIp`), com 429;
  - nada do corpo é gravado além de `_id`, `topic`, `resource`, `user_id`, `application_id`, `attempts`, `sent` e `received`;
  - `resource` aceito só no formato documentado;
  - nunca reflete o corpo na resposta — IT-04, IT-06, IT-07.
- **Privacidade e dados pessoais:** guarda o `user_id` do vendedor, que já é dado da conta conectada. Retenção de 7 dias após o processamento.
- **Disponibilidade e resiliência:**
  - inbox durável;
  - a falha no processamento é retentada pelo Wolverine;
  - a mesma notificação duas vezes não duplica efeito — IT-02;
  - a perda é recuperada pela reconciliação diária (SPEC-0022:IT-05).
- **Acessibilidade (UI):** N/A — sem tela.
- **Custo:** N/A.

## 6. Artefato A — Contrato
**Interface:**
- `POST /api/v1/mercadolivre/notifications`
- evento `MercadoLivreItemChanged` v1

```text
POST /api/v1/mercadolivre/notifications     (anônimo; sem antiforgery; Content-Type application/json; corpo ≤ 16 KB;
                                             fora do documento OpenAPI)
  body (formato documentado pelo ML):
    { "_id": string, "resource": string, "user_id": int64, "topic": string, "application_id": int64,
      "attempts": int, "sent": datetime(UTC), "received": datetime(UTC), "actions"?: [string] }
  200 (corpo vazio) nos casos:
      aceita      -> grava em mercadolivre.notifications e enfileira ProcessMercadoLivreNotification (mesma transação)
      duplicada   -> _id já gravado: nada novo
      ignorada    -> application_id != MercadoLivre:ClientId, ou topic fora de ["items"]: nada gravado
  400 (problem+json, code VALIDATION_FAILED)  JSON inválido ou campo obrigatório ausente/de tipo errado
  413                                         corpo > 16 KB
  429 TOO_MANY_REQUESTS (+ Retry-After)       limite por IP
  503 ML_NOT_CONFIGURED                       ClientId ausente (o webhook não pode validar application_id)

Processamento (ProcessMercadoLivreNotification, fila local durável):
  conta com ml_user_id = user_id inexistente -> marca processada (unknown_user), sem evento
  topic "items" e resource ^/items/(MLB\d+)$ -> publica MercadoLivreItemChanged; outro resource -> marca inválida, sem evento

Persistência: mercadolivre.notifications(id uuid pk, notification_id varchar(64) UNIQUE, topic, resource, ml_user_id bigint,
              application_id bigint, attempts int, sent_at, received_at, accepted_at, processed_at, outcome)

Evento (Vitrine.MercadoLivre.IntegrationEvents), v1:
  MercadoLivreItemChanged { accountId: uuid, workspaceId: uuid, mlUserId: int64, itemId: string, notificationId: string,
                            occurredAt: datetime(UTC) }   // occurredAt = "sent" da notificação
```

**Design:** N/A — sem interface.

**Arquivos/módulos afetados:** ver `touches`.
- `MercadoLivre.Presentation` (endpoint), `MercadoLivre.Infrastructure` (inbox, handler, migração) e `MercadoLivre.IntegrationEvents`.
- `src/Api/Security` (política de rate limit).
- Lista `AnonymousEndpoints` em `tests/Vitrine.IntegrationTests/Security/Support`.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Notificação de anúncio | payload válido, `topic=items`, conta conhecida | 200; linha no inbox; `MercadoLivreItemChanged` publicado com conta e workspace | UT-01, IT-01, CT-01 |
| Duplicada | mesmo `_id` de novo | 200; nenhuma linha nem evento a mais | IT-02 |
| Outro app | `application_id` diferente | 200; nada gravado | IT-03 |
| Tópico não tratado | `topic=questions` etc. | 200; nada gravado | IT-03 |
| Vendedor desconhecido | `user_id` sem conta | 200; processada como `unknown_user`, sem evento | IT-03 |
| Resource inesperado | `topic=items`, `resource` fora do formato | 200; processada como inválida, sem evento | UT-01, IT-03 |
| Payload inválido | JSON quebrado, campo faltando | 400 VALIDATION_FAILED | UT-02, IT-04 |
| Corpo grande | > 16 KB | 413 | IT-04 |
| Rápido | 50 POSTs | p95 < 100 ms | IT-05 |
| Abuso | > limite por IP | 429 com `Retry-After` | IT-06 |
| Anônimo e fora do OpenAPI | sem cookie nem antiforgery | aceito; endpoint na lista `AnonymousEndpoints`; ausente do documento OpenAPI | IT-07 |
| ML não configurado | sem `ClientId` | 503 ML_NOT_CONFIGURED | IT-04 |
| `_id` longo | `_id` com mais de 64 caracteres | 400 VALIDATION_FAILED (`errors._id`), nada gravado | UT-02 |
| Retenção do inbox | processadas há mais de 7 dias; `unknown_user`/`invalid` há mais de 1 dia; não processadas (retentativas esgotadas) aceitas (`accepted_at`) há mais de 7 dias | apagadas pelo job diário; as demais ficam | IT-08 |
| Chave duplicada | JSON com a mesma chave duas vezes | 400 VALIDATION_FAILED | UT-02 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- **CH-01** — Dado a lista `AnonymousEndpoints` atual, quando o teste de autorização por padrão (SPEC-0008) roda antes da mudança, então passa (guarda: passa antes da mudança). Depois da mudança, continua passando, com a única entrada nova.

### 7.2 Testes Unitários
- **UT-01** — Dado `resource` `/items/MLB123`, `/items/MLB123?x`, `/questions/1` e `/items/abc`, quando interpretado para o tópico `items`, então só o primeiro produz `itemId = MLB123`.
- **UT-02** — Dado payloads com cada campo obrigatório ausente ou de tipo errado (`user_id` texto, `_id` vazio, `sent` inválido), quando validados, então são recusados. O payload documentado completo, com `user_id` acima de Int32, é aceito.

### 7.3 Testes de Integração
- **IT-01** — Dado uma conta conectada (builder da SPEC-0020) e Postgres real, quando o ML envia uma notificação `items` válida, então:
  - responde 200 com corpo vazio;
  - a linha fica no inbox;
  - um assinante de teste recebe `MercadoLivreItemChanged` com `accountId`, `workspaceId`, `itemId` e `notificationId` corretos.
- **IT-02** — Dado a mesma notificação (mesmo `_id`) enviada 3 vezes, então as 3 respondem 200, e há 1 linha no inbox e 1 evento publicado.
- **IT-03** — Dado notificações com `application_id` diferente, `topic=questions`, `user_id` sem conta e `resource` inválido, então todas respondem 200:
  - as duas primeiras não gravam nada;
  - as duas últimas são processadas com `outcome` `unknown_user`/`invalid`, sem evento.
- **IT-04** — Dado JSON inválido, payload sem `user_id` e corpo de 17 KB, então respondem 400 VALIDATION_FAILED, 400 e 413. Sem `MercadoLivre:ClientId`, responde 503 ML_NOT_CONFIGURED.
- **IT-05** — Dado Postgres real, quando 50 notificações distintas são enviadas em sequência, então o p95 do tempo de resposta (medido no cliente de teste, mesmo host) é < 100 ms.
- **IT-06** — Dado o limite configurado em 5/min no teste, quando o 6º POST do mesmo IP chega, então responde 429 com `Retry-After`. Outro IP (`TestRemoteIp`, SPEC-0008) não é afetado.
- **IT-07** — Dado a Api, então:
  - o POST sem cookie e sem antiforgery é aceito;
  - o teste de autorização por padrão passa com a entrada `POST /api/v1/mercadolivre/notifications` na lista `AnonymousEndpoints`;
  - o documento OpenAPI gerado não contém esse caminho.

- **IT-08** — Dado linhas do inbox: processada com sucesso há 8 dias e há 1 dia; processada como `unknown_user`/`invalid` há 2 dias e há 1 hora; não processada recebida há 8 dias e há 1 dia — quando a limpeza diária do módulo roda (mensagem recorrente do Wolverine, como `CleanupPendingAuthorizations`), então são apagadas a de sucesso de 8 dias, a `unknown_user`/`invalid` de 2 dias e a não processada de 8 dias; as demais ficam (Emenda v1.2).
### 7.4 Testes de Contrato
- **CT-01** — Dado uma notificação processada, quando um assinante de teste recebe `MercadoLivreItemChanged`, então o payload valida contra o schema v1 (seção 6). A SPEC-0022 consome esse contrato.

### 7.5 Testes E2E
- N/A — o webhook é chamado pelo ML, não pelo vendedor; não há jornada de navegador. O efeito visível (anúncio atualizado) é provado por SPEC-0022 (IT 09).

### 7.6 Outros
- Desempenho: IT-05. Segurança: IT-04, IT-06, IT-07.

**Dublês e dados de teste:** payload de exemplo copiado da documentação de notificações do ML (fixture com URL e data), com variações por teste; builder de conta.

**Ambiente de execução:** IT com Testcontainers (local e CI).

## 8. Plano de Rollout
- **Estratégia:** deploy direto. O ML só chama depois que a URL for cadastrada no DevCenter (go-live).
- **Dados/schema:** migração aditiva no schema `mercadolivre` (tabela `notifications`).
- **Compatibilidade:** endpoint novo, fora do OpenAPI.
- **Observabilidade:**
  - contador `vitrine.mercadolivre.notifications{topic,result}`;
  - alerta sugerido para o go-live (SPEC-0009): p95 da rota acima de 300 ms por 10 min, ou taxa de `invalid` acima de 10%.
- **Rollback:** revert do merge. As notificações não aceitas são retentadas pelo ML por 1 h e, depois disso, recuperadas pela reconciliação.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Validar a origem da notificação? — O ML não documenta assinatura. O payload é tratado como não confiável (só dispara releitura autenticada), com rate limit por IP; a lista de IPs do ML fica opcional no go-live. (Architect, 2026-10-30, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [x] CH-01 escrito, passando no código atual (lista `AnonymousEndpoints` e rate limit por IP atuais), commitado antes de qualquer mudança

**Fase 1: Validação do payload**
- [x] Red: escrever UT-01..UT-02 com a tag `SPEC-0023:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: validação do payload e mapeamento de tópicos
- [x] Refactor mantendo tudo verde

**Fase 2: Webhook e inbox**
- [x] Red: escrever IT-01..IT-07 e CT-01 com a tag `SPEC-0023:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: `POST /api/v1/mercadolivre/notifications` anônimo fora do OpenAPI, 16 KB, 600/min por IP, dedupe por `_id`, inbox e evento `MercadoLivreItemChanged` v1
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
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 2 (red: Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exit) — 189a02e | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  349ms (environment 55%, transform 22%, import 18%, tests 5%, worker 1%)); lint exit 0 ([0m[34mChecked 40 files in 23[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 97%) — 374860e | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 587ms) — 374860e | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED (2ª rodada; notificação forjada → SPEC-0029; retenção v1.2 pelo accepted_at; nit: comparar seller com o ml_user_id lido da conta) — 374860e | 2026-10-30 |
| G5 Integração & CI | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  461ms (environment 61%, transform 20%, import 14%, tests 3%, worker 2%)); test_integration exit 0 (duration: 3m 44s 772ms); test_e2e exit 0 (30 passed (31.3s)); arch_test exit 0 (duration: 1s 788ms); security_scan exit 0 ([90m4:20PM[0m [32mINF[0m [1mno leaks found[0m); nota: PR #51 CI 8/8 verde + CodeQL (runs/41112611794) — 0051d64 | 2026-10-30 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-10-30 |
| G6 Deploy | PASS | MANUAL: Deploy local prod-like (deploy/deploy.sh 963c0fc --env-file deploy/local/.env): health 200 em 5 s; migração do inbox aplicada; smoke @smoke 2/2; POST /api/v1/mercadolivre/notifications sem credenciais do ML → 503 ML_NOT_CONFIGURED (esperado no local), GET → 404; 0 logs Error/Critical; staging/produção na SPEC-0009 | 2026-10-30 |
| G7 Pronto & Docs | PENDING | | |

## 13. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 14. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Diz o que foi feito, como, e prova que foi resolvido. Para status implemented o validate exige todas as subseções preenchidas, todo teste do plano com PASS + evidência e a Definição de Pronto toda marcada. -->

### O que foi entregue
Webhook anônimo `POST /api/v1/mercadolivre/notifications` (fora do OpenAPI) que recebe as notificações do ML, responde 200 em < 100 ms gravando no inbox `mercadolivre.notifications` (dedupe por `_id`) e processa depois, de forma durável: para o tópico `items` de uma conta conhecida, publica `MercadoLivreItemChanged` v1, que o Listings usa para reler o anúncio. Corpo ≤ 16 KB, 600/min por IP, JSON estrito (tipos, tamanho de `_id`, chave duplicada), sem refletir o corpo nem enviar cookie. Limpeza diária do inbox.

### Como foi feito
- Application: `NotificationPayload.Parse` estrito, `NotificationResource.TryGetItemId` (`\A/items/(MLB[0-9]+)\z`), `ReceiveNotificationHandler` (503 → 400 → `ignored_app` → `ignored_topic` → inbox).
- Infrastructure: migração do inbox (UNIQUE em `notification_id`); `INSERT … ON CONFLICT DO NOTHING` com `ProcessMercadoLivreNotification` no outbox na mesma transação; processamento com `FOR UPDATE`, idempotente; `CleanupNotificationsInbox` recorrente (03:00 UTC); métrica com `topic` limitado a `items|other|unknown`.
- Api: partição própria de rate limit por nome de endpoint (`RateLimiting:MercadoLivreNotificationsPerIp:PermitLimit`); 413 com o código `PAYLOAD_TOO_LARGE`.
- Emendas: v1.1 (IT-08 de retenção, `outcome` `item_changed`, regras da métrica, `_id` ≤ 64, nome da chave, fixture fictícia) e v1.2 (retenção de 1 dia para `unknown_user`/`invalid` e de 7 dias pelo `accepted_at` para não processadas; chave duplicada no UT-02).
- G4: o achado de segurança (notificação forjada gravando anúncio alheio) foi corrigido na SPEC-0029; o defeito de geração de código do Wolverine com o gateway real, revelado aqui, foi corrigido na SPEC-0028.
- Desvio conhecido: o Wolverine 6.59 avisa que `ClassicCombineIntoOneLogicalHandler` some no 7.0 (handlers do mesmo tipo de mensagem combinados) — tratar na atualização para o 7.0.

### Prova de Correção
N/A — `type: feature`.

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| CH-01 | Lista de endpoints anônimos e rate limit por IP antes do webhook | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| UT-01 | Resource `/items/MLB…` reconhecido só no formato exato | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| UT-02 | Payload estrito: tipos, `_id` ≤ 64, int64, chave duplicada, campos extras ignorados | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-01 | Notificação de item de conta conhecida → 200, inbox `item_changed` e `MercadoLivreItemChanged` publicado | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-02 | Duplicada pelo `_id` → 200 sem linha nem evento a mais | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-03 | Outro app, tópico não tratado, vendedor desconhecido e resource inválido; métricas por resultado | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-04 | 400 VALIDATION_FAILED, 413 acima de 16 KB (com e sem Content-Length), 503 sem ClientId | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-05 | p95 < 100 ms em 50 POSTs | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-06 | Rate limit por IP com 429 e Retry-After; outro IP não é afetado | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-07 | Anônimo, sem antiforgery, na lista `AnonymousEndpoints`, fora do OpenAPI, sem cookie | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| IT-08 | Retenção do inbox (7 dias, 1 dia para recusadas, 7 dias pelo `accepted_at` para não processadas) e agendamento | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |
| CT-01 | `MercadoLivreItemChanged` publicado conforme o schema v1 | PASS | CI PR #51 runs/41112611794 (374860e); main 963c0fc |

### Definição de Pronto
- [x] Todos os testes do plano passando e listados na Verificação
- [x] Todo comportamento do Mapa de Comportamentos coberto e verificado
- [x] Suíte completa, arquitetura e CI verdes no resultado integrado (G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão arquitetural existente mantido, ou desvio coberto por ADR aprovado
- [x] Requisitos não-funcionais medidos com evidência (ou N/A justificado) — p95 < 100 ms (IT-05), corpo ≤ 16 KB e rate limit por IP (IT-04/IT-06)
- [x] Disponível no ambiente-alvo via pipeline, com smoke/E2E passando no ambiente (G6) — ambiente local prod-like; staging/produção na SPEC-0009
- [x] Observabilidade e rollback prontos conforme o Plano de Rollout — métrica `vitrine.mercadolivre.notifications{topic,result}`; rollback por revert (tabela nova)
- [x] Documentação raiz e CHANGELOG atualizados (G7)
- [x] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7) — N/A: sem `living_docs`; README atualizado
- [x] Pendências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, imagem `ghcr.io/thomasravache/vitrine-api:963c0fc`, 2026-10-30, `deploy/deploy.sh` (health 200 em 5 s, smoke 2/2, 0 logs Error/Critical). Staging/produção na SPEC-0009.

### Pendências
- Go-live (SPEC-0009): URL do webhook no DevCenter do ML; allowlist dos IPs do ML no Caddy passa a ser obrigatória; rate limit por /64 em IPv6 (hoje por endereço, herdado da SPEC-0008).
- Atualização do Wolverine para 7.0: revisar handlers combinados.
- Nit do G4: comparar o vendedor com o `ml_user_id` lido da conta no `ItemChangedHandler` (defensivo; hoje equivalente).

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0023`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (contrato esclarecido) | 2026-10-30 | (1) IT-08 cobre a retenção de 7 dias do inbox; (2) `outcome` de sucesso = `item_changed`; (3) métrica: `accepted`/`duplicate`/`ignored_app`/`ignored_topic` contados no recebimento, `invalid` no 400 e no processamento de resource inválido, `unknown_user` no processamento — uma notificação aceita pode contar `accepted` e depois `unknown_user`/`invalid`; (4) `_id` com mais de 64 caracteres → 400 VALIDATION_FAILED; (5) chave `RateLimiting:MercadoLivreNotificationsPerIp:PermitLimit`; (6) fixture no formato documentado com valores fictícios até a SPEC-0024 trazer a cópia oficial | Lacunas apontadas pelo Test-writer no G1 | SPEC-0024 (fixture) | thomas (piloto automático do épico SPEC-0019) |
| v1.2 | 2026-10-30 | Retenção: `unknown_user`/`invalid` por 1 dia e não processadas (retentativas esgotadas) por 7 dias pelo `accepted_at` (hora do servidor; o `received` vem do payload e pode ser forjado), para limitar o volume do inbox sob abuso; UT-02 cobre JSON com chave duplicada | Achados 2–4 do G4 | nenhuma | thomas (piloto automático do épico SPEC-0019) |

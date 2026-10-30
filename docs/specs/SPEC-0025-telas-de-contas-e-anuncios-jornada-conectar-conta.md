---
id: SPEC-0025
title: Telas de contas e anúncios (jornada conectar-conta)
tier: full
type: feature
user_facing: true
status: in-progress
created: 2026-10-30
parent: SPEC-0019
depends_on: [SPEC-0024]
consumes_contract: [SPEC-0020@1, SPEC-0022@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [web/src/features/**, web/src/router.tsx, web/src/ui/**, web/src/App.tsx, web/e2e/**, web/package.json, web/package-lock.json, src/Api/Security/SecurityHeaders.cs, tests/Vitrine.IntegrationTests/Security/**, .gitleaksignore]
adrs: [ADR-0007, ADR-0013]
external: []
size: M
approved_by: thomas
approved_at: 2026-10-30
---
# SPEC-0025 — Telas de contas e anúncios (jornada conectar-conta)

## 1. Visão Geral
Entrega a jornada `conectar-conta` na SPA, no celular e no desktop:
- **Painel:** o cartão "Conectar conta do Mercado Livre (em breve)" passa a funcionar e mostra o resumo dos anúncios.
- **Contas** (`/contas`): conectar, reconectar e desconectar.
- **Retorno do OAuth** (`/contas/mercado-livre/retorno`).
- **Anúncios** (`/anuncios`): busca, filtros, "Carregar mais" e "Sincronizar agora".
- **Navegação:** inferior no celular, lateral no desktop.

A jornada fica provada por E2E contra o simulador (SPEC-0024).

## 2. Motivação & Escopo
**Motivação:** é a primeira entrega de valor visível ao vendedor e a jornada crítica `conectar-conta` do produto. Ela também valida, ponta a ponta, as SPEC-0020 a 0024.

**Objetivos (dentro do escopo):**
- Rotas protegidas `/contas`, `/contas/mercado-livre/retorno` e `/anuncios`, mais o `/painel` atualizado, no `router.tsx` (TanStack Router/Query, cliente orval — ADR-0013).
- Navegação do `AppShell`: inferior (Painel, Anúncios, Contas) abaixo de 1024 px e lateral (com "Sair") a partir de 1024 px, como nos desenhos da SPEC-0006.
- Textos e erros em pt-BR para cada `code` dos contratos SPEC-0020@1 e SPEC-0022@1.
- Ações de Owner (conectar, reconectar, desconectar) ocultas para `Member`, com texto "Só o dono do workspace pode conectar contas".
- Desconectar com diálogo de confirmação acessível: "Desconectar LOJA_X? Os anúncios dessa conta saem do Vitrine. Você pode conectar de novo depois."
- Enquanto alguma conta está `running`, as telas consultam o resumo a cada 5 s e param quando todas saem de `running`.
- Miniaturas do ML: a CSP ganha `img-src 'self' https://*.mlstatic.com` (emenda da SPEC-0008). Sem miniatura, um ícone neutro.
- E2E da jornada, com `[jornada: conectar-conta]` em `critical_journeys` (`sdd-config.yml`), e o `smoke_test` com `ML_SIMULATOR_URL` para o ambiente local.

**Não-objetivos (fora do escopo):**
- Detalhe do anúncio, edição e melhoria: épico "Melhorar anúncios".
- Ordenação por coluna, exportação e seleção em massa.
- Notificações em tempo real (push/SSE): a atualização vem do polling durante a sincronização e do recarregamento da tela.
- Convite de membros (épico "Contas e acesso"). A regra de papel é testada com um Member criado no banco nos testes de API (SPEC-0020:IT-06) e com dados simulados nos testes de componente.

## 3. Dependências
- **Implementações necessárias:** SPEC-0024, porque o E2E da jornada precisa do simulador rodando no AppHost e no ambiente local.
- **Contratos consumidos:**
  - SPEC-0020@1: endpoints de conexão e contas, códigos de erro.
  - SPEC-0022@1: `GET /listings`, `GET /listings/summary` e `POST /listings/sync`.
  - O cliente é gerado do OpenAPI; os testes de componente usam dados no formato desses contratos.
- **Pré-requisitos externos:** N/A.

## 4. Decisão Arquitetural
**Contexto:**
- As telas de referência são as da SPEC-0006, em `web/src/features/auth/*` e `web/src/features/dashboard/{AppShell,DashboardPage}.tsx`.
- Os componentes de referência são `web/src/ui/{Button,TextField,Alert}.tsx`.
- Rotas protegidas como em `router.tsx` (`dashboardRoute`); cliente em `web/src/api/generated` (orval); E2E em `web/e2e/acesso.spec.ts`, com axe (`web/e2e/support/axe.ts`).
- Cabeçalhos em `src/Api/Security/SecurityHeaders.cs` (SPEC-0008).

**Decisão:**
- Novas features em `web/src/features/accounts` e `web/src/features/listings`, com os mesmos componentes de UI.
- Lista em cartões abaixo de 1024 px e tabela a partir de 1024 px, renderizadas a partir dos mesmos dados.
- A busca dispara com 300 ms de espera após a digitação.
- Filtros e busca na URL (search params do TanStack Router), para o link funcionar e o botão voltar respeitar a navegação.

**Justificativa:** segue o padrão da SPA (ADR-0013) e o design system do Stitch. Os filtros na URL deixam o estado compartilhável e testável.

**Desvio do padrão existente:** nenhum. A mudança na CSP é emenda da SPEC-0008 (v1.3), restrita a `img-src` para `https://*.mlstatic.com`.

**Alternativas descartadas:**
- Baixar as miniaturas para o nosso servidor (proxy de imagens): custo de banda e armazenamento sem ganho agora.
- Paginação numerada: o cursor da SPEC-0022 não tem total por página, e "Carregar mais" funciona melhor no celular.

**ADRs:** ADR-0007, ADR-0013.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:**
  - a tela Anúncios mostra a 1ª página em < 1 s no ambiente local (E2E com o simulador);
  - a busca não faz mais de 1 requisição por pausa de digitação (UT-03);
  - o bundle da SPA cresce no máximo 40 KB gzip (conferido pelo build no G2).
- **Segurança:**
  - nenhum token chega à SPA (contratos SPEC-0020/0022);
  - CSP `img-src` liberada só para `https://*.mlstatic.com` — IT-01;
  - a página de retorno só aceita `code`/`state`/`error` da query e limpa a URL depois de usar (`history.replaceState`), para o `code` não ficar no histórico — UT-02.
- **Privacidade e dados pessoais:** N/A — exibe só dados da conta e dos anúncios do próprio workspace.
- **Disponibilidade e resiliência:**
  - erro de rede ou `ML_UNAVAILABLE` mostra uma mensagem com "Tentar de novo";
  - a tela Anúncios continua mostrando os dados locais quando a sincronização falha (`failed`), com aviso.
- **Acessibilidade (UI):**
  - WCAG 2.2 AA em Painel, Contas, Retorno e Anúncios, nas duas larguras;
  - axe sem violações sérias ou críticas;
  - diálogo de confirmação com foco preso e retorno do foco;
  - chips de filtro como grupo de botões com `aria-pressed`;
  - selos de estado com texto, não só cor;
  - verificação: E2E-01/03 (axe) e UT-05.
- **Custo:** N/A.

## 6. Artefato A — Contrato
**Interface:** telas `/painel`, `/contas`, `/contas/mercado-livre/retorno` e `/anuncios`; navegação do `AppShell`; cabeçalho CSP.

```text
Todas as rotas abaixo exigem sessão (sem sessão -> /entrar, como /painel na SPEC-0006).

/painel
  sem contas: cartão "Conecte sua conta do Mercado Livre" + texto curto + botão primário "Conectar conta" (Owner)
  com contas: cartão "{total} anúncios · sincronizado há {tempo}" + link "Ver anúncios"; para cada conta em
              reauthorization_required, alerta âmbar "A conta {nickname} precisa ser reconectada" + link "Ir para Contas"
  importando (alguma conta running): "Importando seus anúncios…" com indicador de progresso indeterminado

/contas
  título "Contas do Mercado Livre"; texto "Conecte as contas da sua loja para o Vitrine ler seus anúncios. Você pode
  desconectar quando quiser."
  cartão por conta: nickname; selo "Conectada" (primária) | "Reconectar necessário" (âmbar, + "O acesso expirou. Conecte de
  novo para continuar." + botão "Reconectar"); "Anúncios: {itemCount} · sincronizado há {tempo}" (ou "Importando…",
  ou "Última sincronização falhou" com o horário); botão de texto "Desconectar" -> diálogo de confirmação -> DELETE
  botão primário "Conectar conta do Mercado Livre" + nota "Você será levado ao Mercado Livre para autorizar. O Vitrine
  nunca vê sua senha."
  Conectar/Reconectar: POST /connections -> window.location.assign(authorizationUrl)
  Member: botões ocultos; texto "Só o dono do workspace pode conectar ou desconectar contas."

/contas/mercado-livre/retorno?code&state | ?error&state
  com code+state: POST /connections/complete; enquanto isso "Concluindo a conexão…"
    201/200 -> /contas com aviso de sucesso "Conta {nickname} conectada. Estamos importando seus anúncios."
  com error=access_denied -> "Você cancelou a autorização no Mercado Livre." + botão "Voltar para Contas"
  mensagens por code (com botão "Tentar de novo" que reinicia a conexão, exceto onde indicado):
    OAUTH_STATE_INVALID, OAUTH_CODE_INVALID -> "O link de autorização expirou ou já foi usado. Tente conectar de novo."
    ML_ACCOUNT_IN_USE      -> "Essa conta do Mercado Livre já está conectada em outro workspace do Vitrine." (sem tentar de novo)
    ML_SITE_NOT_SUPPORTED  -> "Por enquanto o Vitrine funciona só com contas do Mercado Livre Brasil." (sem tentar de novo)
    ML_UNAVAILABLE         -> "O Mercado Livre não respondeu. Tente de novo em alguns minutos."
    ML_NOT_CONFIGURED      -> "Conexão com o Mercado Livre indisponível no momento." (sem tentar de novo)
    FORBIDDEN              -> "Só o dono do workspace pode conectar contas." (sem tentar de novo)
  query sem code/state/error -> redireciona para /contas
  após ler a query: history.replaceState remove code/state da URL

/anuncios?status=active|paused|closed|all&conta={accountId}&q={texto}
  cabeçalho "Anúncios", "{total} anúncios · sincronizado há {tempo}", botão "Sincronizar agora" (POST /listings/sync)
    429 SYNC_TOO_SOON -> "Você sincronizou há pouco. Tente de novo em {n} min."; 409 -> "Reconecte a conta para sincronizar."
  campo "Buscar por título ou código" (300 ms); chips Ativos | Pausados | Encerrados | Todos (aria-pressed);
  seletor "Conta" só com 2+ contas
  < 1024 px: cartões (miniatura 64 px, título 2 linhas, preço R$ pt-BR, "Estoque {n} · {m} vendidos", selo de estado,
             link "Ver no Mercado Livre" com aria-label e target _blank rel noopener)
  ≥ 1024 px: tabela (Miniatura, Título + código, Preço, Estoque, Vendidos, Estado, Conta, link)
  "Carregar mais" enquanto houver nextCursor
  vazios: sem contas -> "Nenhum anúncio ainda. Conecte uma conta do Mercado Livre para ver seus anúncios aqui." + "Conectar conta";
          sem resultado -> "Nenhum anúncio encontrado para esses filtros." + "Limpar filtros"; importando -> "Importando seus anúncios…"

Navegação (AppShell): < 1024 px barra inferior Painel | Anúncios | Contas (aria-current na ativa); ≥ 1024 px menu lateral com os
  mesmos itens + "Sair"

CSP (src/Api/Security/SecurityHeaders.cs): acrescenta "img-src 'self' https://*.mlstatic.com" (demais diretivas inalteradas)
```

**Design:** Stitch, projeto "Vitrine" (`projects/6635450613032213900`), sistema de design `assets/15895198957828239862`.
- **Mobile:**
  - Contas do Mercado Livre (`screens/c7b13447bf704649aff4f51acde7f666`);
  - Anúncios (`screens/4377f3debac64924b8f29eee95737cbe`).
- **Desktop:** Anúncios (`screens/f43e752c06254300b0b9647c34239144`).
- **Sem desenho:** Contas no desktop segue o cartão do mobile, centralizado em até 720 px, com o menu lateral. O Painel segue os desenhos da SPEC-0006, trocando o cartão "em breve".

Diferenças em relação aos desenhos, por escopo ou regra do produto:
- sem botão voltar no topo de Contas;
- sem subtítulo "Gestão ML" e sem marca com ícone de loja no menu lateral (padrão da SPEC-0006);
- sem contagem nos chips (o resumo traz o total por status, mas os chips mostram só o nome, para não desalinhar com a busca);
- sem "Mostrando N de M";
- sem selo MLB sobre a miniatura (o código fica abaixo do título);
- sem bloco demonstrativo de estado vazio junto da lista (o vazio é um estado à parte).

**Arquivos/módulos afetados:** ver `touches`.
- `web/src/features/{accounts,listings,dashboard}/**`, `web/src/router.tsx`, `web/src/ui/**`
- `web/e2e/**`
- `src/Api/Security/SecurityHeaders.cs` e o teste de cabeçalhos
- `docs/specs/sdd-config.yml` (jornada e smoke, editado pelo Architect)

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Conectar e ver anúncios | Owner sem contas | autoriza no ML, volta, conta "Conectada", anúncios listados | E2E-01 |
| Painel com e sem conta | 0 ou 1+ contas | cartão de conectar ou resumo com link | UT-04, E2E-01 |
| Filtrar e buscar | chips, busca, conta | lista filtrada; filtros na URL | UT-03, E2E-01 |
| Carregar mais | `nextCursor` presente | próxima página anexada | UT-04, E2E-01 |
| Reconectar | conta revogada no ML | selo âmbar e "Reconectar"; após reconectar, "Conectada" | E2E-02 |
| Desconectar | confirmar no diálogo | conta some; Anúncios no estado vazio | UT-05, E2E-03 |
| Cancelar autorização | "Cancelar" no ML | mensagem de cancelamento e volta para Contas | UT-01, E2E-04 |
| Erros do retorno | cada `code` | mensagem e ação corretas; `code` removido da URL | UT-01, UT-02, E2E-04 |
| Conta de outro país | vendedor MLA | "Por enquanto o Vitrine funciona só com contas do Mercado Livre Brasil." | UT-01, E2E-04 |
| Sincronizar agora | botão | aguarda; 429 mostra "Tente de novo em {n} min" | UT-01, E2E-05 |
| Anúncio alterado no ML | notificação do simulador | novo título aparece após recarregar | E2E-05 |
| Member | papel Member | sem botões de conectar/desconectar, com texto explicativo | UT-05 |
| Miniaturas | `thumbnailUrl` de `*.mlstatic.com` ou null | imagem permitida pela CSP ou ícone neutro | IT-01, UT-04 |
| Acessibilidade | 4 telas, 2 larguras | axe sem violações sérias/críticas; diálogo com foco preso | UT-05, E2E-01, E2E-03 |

## 7. Artefato B — Plano de Testes (TDD)

### 7.1 Testes de Caracterização
- **CH-01** — Dado o E2E atual da jornada `acesso` (SPEC-0006 (E2E 01)), quando roda depois da troca do cartão "em breve" do painel, então continua passando (guarda: passa antes da mudança). Ajuste permitido: só o texto do cartão, citado na emenda do E2E.

### 7.2 Testes Unitários
<!-- vitest + @testing-library/react (happy-dom), como web/src/App.test.tsx. -->
- **UT-01** — Dado cada `code` dos contratos (`OAUTH_STATE_INVALID`, `OAUTH_CODE_INVALID`, `ML_ACCOUNT_IN_USE`, `ML_SITE_NOT_SUPPORTED`, `ML_UNAVAILABLE`, `ML_NOT_CONFIGURED`, `FORBIDDEN`, `SYNC_TOO_SOON` com `retryAfterSeconds`, `ACCOUNT_REAUTHORIZATION_REQUIRED`) e um desconhecido, quando mapeado, então devolve a mensagem da seção 6, com ou sem "Tentar de novo". O desconhecido recebe uma mensagem genérica.
- **UT-02** — Dado a página de retorno com `?code&state`, `?error=access_denied&state` ou sem parâmetros, quando montada (cliente simulado), então:
  - chama `complete` só no 1º caso;
  - remove `code`/`state` da URL com `replaceState`;
  - redireciona para `/contas` no 3º caso.
- **UT-03** — Dado o campo de busca, quando o usuário digita "fone" rapidamente, então uma única consulta sai 300 ms depois da última tecla, e os filtros (status, conta, q) ficam refletidos na URL.
- **UT-04** — Dado dados de anúncios (com e sem miniatura, preço decimal, `nextCursor`) e resumos (0 contas, 1 conta `running`, 1 conta `reauthorization_required`), então:
  - a lista mostra preço em `R$ 1.234,56`, "Estoque 12 · 34 vendidos", o selo com texto e o ícone neutro sem miniatura;
  - "Carregar mais" só aparece com `nextCursor`;
  - o painel mostra o cartão certo para cada resumo;
  - o tempo relativo aparece como "sincronizado há 5 min".
- **UT-05** — Dado a tela Contas, então:
  - com papel `Member`, os botões de conectar, reconectar e desconectar não aparecem e o texto explicativo sim;
  - com `Owner`, "Desconectar" abre o diálogo, com foco no botão de cancelar, Esc fecha e devolve o foco, e confirmar chama o `DELETE`.

### 7.3 Testes de Integração
- **IT-01** — Dado a Api (`ApiFactory`), quando qualquer rota responde, então a CSP contém `img-src 'self' https://*.mlstatic.com`, e as demais diretivas continuam iguais às da SPEC-0008. É o teste existente de cabeçalhos, atualizado pela emenda.

### 7.4 Testes de Contrato
- **CT-01** — Dado o cliente orval regenerado a partir do OpenAPI das SPEC-0020/0022, quando o `tsc --noEmit` e o drift check do CI (SPEC-0011) rodam, então as telas compilam contra os tipos gerados, e o cliente versionado está em dia. É o lado consumidor dos contratos SPEC-0020@1 e SPEC-0022@1.

### 7.5 Testes E2E
<!-- Playwright, projetos desktop e mobile, AppHost com o simulador (SPEC-0024). Cada teste cria o próprio vendedor (POST /__sim/sellers) e o próprio usuário. -->
- **E2E-01** — Dado um vendedor novo no simulador (15 ativos, 6 pausados, 4 encerrados), quando o usuário:
  1. se cadastra;
  2. clica em "Conectar conta" no painel;
  3. escolhe o vendedor e clica em "Autorizar" na página do simulador;
  4. volta e vê a conta "Conectada" em Contas;
  5. abre Anúncios e vê 15 ativos;
  6. filtra "Pausados" (6), busca por um título e carrega mais;

  então tudo isso acontece, e o axe não acha violações sérias ou críticas no Painel, em Contas, no Retorno e em Anúncios. [jornada: conectar-conta] @smoke
- **E2E-02** — Dado uma conta conectada, quando o simulador revoga os tokens e expira o access token (`/__sim`) e o usuário clica em "Sincronizar agora", então:
  - a conta aparece com "Reconectar necessário";
  - "Reconectar" leva de novo ao simulador;
  - após autorizar, ela volta a "Conectada".
- **E2E-03** — Dado uma conta conectada com anúncios, quando o usuário clica em "Desconectar" e confirma no diálogo, então a conta some de Contas, e Anúncios mostra o estado vazio com "Conectar conta". O axe roda com o diálogo aberto.
- **E2E-04** — Dado a página do simulador, então:
  - "Cancelar" leva à mensagem "Você cancelou a autorização no Mercado Livre.";
  - autorizar um vendedor MLA leva a "Por enquanto o Vitrine funciona só com contas do Mercado Livre Brasil.";
  - nos dois casos, a URL final não contém `code`.
- **E2E-05** — Dado uma conta conectada, quando o simulador muda o título de um anúncio e envia a notificação (`/__sim/notify`), então o novo título aparece em Anúncios após recarregar (espera de até 15 s). Um 2º "Sincronizar agora" imediato mostra "Tente de novo em".

### 7.6 Outros
- Acessibilidade: axe no E2E-01 e E2E-03 (critério: zero sérias/críticas). A página do simulador fica excluída do critério de produto e é verificada à parte, como aviso.
- Tamanho do bundle: saída do `npm run build` registrada no G2, com crescimento ≤ 40 KB gzip.

**Dublês e dados de teste:**
- vendedores criados por teste no simulador;
- usuários com e-mail único (helpers do `acesso.spec.ts`);
- nos testes de componente, dados com o formato dos contratos e o cliente HTTP simulado (padrão do `App.test.tsx`).

**Ambiente de execução:**
- UT com vitest (local e CI);
- E2E contra o AppHost com o simulador (local e CI);
- `@smoke` (E2E-01) contra o ambiente local prod-like com o perfil `ml-simulator` no G6, com `ML_SIMULATOR_URL=https://ml-simulador.vitrine.localhost`.

## 8. Plano de Rollout
- **Estratégia:** deploy direto. Sem ML configurado (staging/produção antes do app no DevCenter), "Conectar" mostra "Conexão com o Mercado Livre indisponível no momento".
- **Dados/schema:** N/A.
- **Compatibilidade:** o cartão "em breve" do painel é substituído e o E2E da jornada `acesso` é ajustado só nesse texto (CH-01). A CSP passa a permitir as miniaturas do ML (emenda da SPEC-0008).
- **Observabilidade:** erros de tela pelo Sentry da SPA (SPEC-0007, quando configurado); métricas de conexão e sincronização no backend (SPEC-0020/0022).
- **Rollback:** revert do merge. As telas antigas voltam, e os dados no backend não são afetados.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Miniaturas direto do ML ou por proxy? — Direto de `https://*.mlstatic.com`, liberado na CSP (emenda da SPEC-0008 v1.3). Proxy só se houver motivo (custo, privacidade). (Architect, 2026-10-30, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [x] CH-01 escrito, passando no código atual (rotas e CSP atuais da SPA), commitado antes de qualquer mudança

**Fase 1: Componentes e CSP**
- [x] Red: escrever UT-01..UT-05, IT-01 e CT-01 com a tag `SPEC-0025:<ID>` e confirmar que falham pelo motivo certo
- [x] Green: telas Painel, Contas, retorno do OAuth e Anúncios (Stitch), CSP com `img-src https://*.mlstatic.com`
- [x] Refactor mantendo tudo verde
- [x] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase 2: Jornada E2E**
- [x] Red: E2E-01..E2E-05 falhando pelo motivo certo
- [x] Green: jornada `conectar-conta` completa passando localmente contra o simulador

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
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 1 (red: ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯[32/34]⎯) — d65dfcb | 2026-10-30 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (at least ~698ms faster with isolate: false — reuses workers across files instead of one pe); lint exit 0 ([0m[34mChecked 66 files in 40[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (Coverage: 100%) — 5150604 | 2026-10-30 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 953ms) — 5150604 | 2026-10-30 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED (2ª rodada; notas do G4 corrigidas com testes; Sentry → SPEC-0030; critério de polling e limites do keepalive/dedupe como pendências) — 5150604 | 2026-10-30 |
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
- [ ] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7)
- [ ] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0025`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 (esclarecimento, contrato inalterado) | 2026-10-30 | (1) Member em `/contas`: "Só o dono do workspace pode conectar ou desconectar contas." (texto do §6 prevalece); no retorno com `FORBIDDEN`: "Só o dono do workspace pode conectar contas."; (2) "Tentar de novo" só em `OAUTH_*` e `ML_UNAVAILABLE`; code desconhecido mostra alerta genérico sem o code cru; (3) página de Anúncios com 20 itens (padrão da SPEC-0022) e "Carregar mais"; (4) E2E-05: o 2º "Sincronizar agora" acontece logo após a 1ª sincronização manual concluir (o `SYNC_TOO_SOON` conta a partir da conclusão); (5) E2E intercepta `*.mlstatic.com` com PNG fixo | Pontos em aberto apontados pelo Test-writer no G1 | nenhuma | thomas (piloto automático do épico SPEC-0019) |
| v1.2 (contrato inalterado) | 2026-10-30 | `touches` inclui `.gitleaksignore`: fingerprint do falso positivo `generic-api-key` na senha fixa de teste do E2E (`web/e2e/support/vitrine.ts`, o mesmo valor já ignorado em `acesso.spec.ts`), conforme a ADR-0006 | gitleaks do CI no PR #54 | nenhuma | thomas (piloto automático do épico SPEC-0019) |

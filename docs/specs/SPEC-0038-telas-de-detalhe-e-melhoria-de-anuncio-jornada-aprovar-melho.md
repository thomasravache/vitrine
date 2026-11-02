---
id: SPEC-0038
title: Telas de detalhe e melhoria de anúncio (jornada aprovar-melhoria)
tier: full
type: feature
user_facing: true
status: approved
created: 2026-11-02
parent: SPEC-0032
depends_on: [SPEC-0034, SPEC-0036]
consumes_contract: [SPEC-0035@1, SPEC-0036@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [web/src/features/listings/**, web/src/router.tsx, web/src/ui/**, web/e2e/aprovar-melhoria.spec.ts, web/e2e/support/ml-simulator.ts, web/e2e/support/vitrine.ts, tests/Vitrine.IntegrationTests/Security/SpaFallbackTests.cs]
adrs: [ADR-0002, ADR-0013, ADR-0017]
external: []
size: M
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0038 — Telas de detalhe e melhoria de anúncio (jornada aprovar-melhoria)

<!-- type: feature | fix | refactor | migration | foundation. user_facing: true quando a mudança altera uma jornada do usuário (UI ou API pública) — exige teste E2E. Substitua todos os marcadores com chaves duplas: o G0 (`spec_graph.py validate`) reprova a spec enquanto restar algum. As seções de Checklist em diante são preenchidas depois da aprovação, sem marcadores. -->

## 1. Visão Geral
Entrega a jornada `aprovar-melhoria` na SPA, no celular e no desktop:
- **Detalhe do anúncio** (`/anuncios/$itemId`): dados, atributos (obrigatórios faltando em destaque), descrição, o botão "Melhorar com IA" e a cota do mês.
- **Melhoria** (`/anuncios/$itemId/melhoria`):
  - "Gerando sugestão…" enquanto a IA trabalha (polling);
  - comparação antes e depois por campo, com a diferença destacada, edição, seleção de campos, campos bloqueados com motivo e avisos de grounding;
  - "Publicar no Mercado Livre", "Descartar" e "Gerar de novo";
  - resultado por campo: publicado, recusado com o motivo ou não enviado.
- Link "Melhorar" em cada anúncio da lista de Anúncios.

A jornada fica provada por E2E contra o simulador do ML (SPEC-0034) e a IA simulada (ADR-0017), sem custo.

## 2. Motivação & Escopo
**Motivação:** é a entrega de valor do épico SPEC-0032. O vendedor só confia numa sugestão de IA se vê exatamente o que muda, pode corrigir e decide o que vai para o Mercado Livre (ADR-0002: nada é alterado sem aprovação).

**Objetivos (dentro do escopo):**
- Rotas protegidas `/anuncios/$itemId` e `/anuncios/$itemId/melhoria` no `router.tsx` (TanStack Router/Query, cliente orval — ADR-0013).
- Textos e erros em pt-BR para cada `code` dos contratos SPEC-0035@1 e SPEC-0036@1 (§6).
- Polling do `latest` a cada 2 s enquanto `generating`, parando no estado final; após 2 min, aviso "Está demorando mais que o normal" com "Continuar esperando".
- Comparação por campo, com a diferença por palavra feita na própria SPA (sem biblioteca nova).
- Seleção padrão: campos sugeridos e sem aviso de grounding vêm marcados; com aviso, desmarcados; bloqueados e descartados, desabilitados com o motivo.
- Edição com contador de caracteres e limite da categoria; atributo de lista com seletor dos valores permitidos; `number_unit` com número + unidade permitida.
- Cota "{restante} de {limite} melhorias este mês" no detalhe e na melhoria; cota esgotada desabilita "Melhorar com IA" com o motivo e a data de renovação.
- Link "Melhorar" nos cartões (< 1024 px) e na tabela (≥ 1024 px) de Anúncios, só para anúncios ativos.
- E2E da jornada com `[jornada: aprovar-melhoria]` (o Architect ativa a jornada em `critical_journeys` no `docs/specs/sdd-config.yml`) e `@smoke` no E2E-01.
- Helpers do E2E para o simulador (vendas, catálogo, falha forçada).

**Não-objetivos (fora do escopo):**
- Desfazer uma publicação, histórico de melhorias e publicação em lote.
- Editar fotos, preço e estoque.
- Nota de qualidade do ML (épico futuro).
- Notificação em tempo real (push/SSE): polling basta para uma geração de segundos.

## 3. Dependências
- **Implementações necessárias:**
  - SPEC-0034, porque o E2E precisa do simulador servindo detalhe, categorias e edição, com a massa de vendas e catálogo e os controles de falha forçada;
  - SPEC-0036 (e, por ela, a SPEC-0035), porque o E2E da jornada roda contra a Api real: pedir, gerar, publicar e descartar precisam estar implementados para o E2E passar no G2.
- **Contratos consumidos:**
  - SPEC-0035@1: `GET .../details`, `POST .../improvements`, `GET .../improvements/latest`, `GET /listings/improvements/quota` e os códigos;
  - SPEC-0036@1: `POST .../apply`, `POST .../discard`, o `result` por campo e os códigos.
  - O cliente vem do OpenAPI (orval); os testes de componente usam dados no formato desses contratos.
- **Pré-requisitos externos:** N/A. A IA é a simulada em dev, CI, E2E e ambiente local (ADR-0017).

## 4. Decisão Arquitetural
**Contexto:** segue as telas da SPEC-0025:
- `web/src/features/listings/{ListingsPage,ListingsList}.tsx` e `queries.ts` (lista em cartões e tabela, TanStack Query sobre o cliente orval);
- `web/src/features/accounts/errorMessages.ts` (mensagem por `code`) e `AccountsPage.tsx` (diálogo acessível);
- `web/src/router.tsx` (`authenticatedRoute`, search params);
- `web/src/ui/{Button,TextField,Alert}.tsx`;
- polling do resumo durante a sincronização (SPEC-0025);
- E2E em `web/e2e/conectar-conta.spec.ts`, com `support/ml-simulator.ts` (`createSimSeller`, `stubMlThumbnails`), `support/vitrine.ts` e axe (`support/axe.ts`).

**Decisão:**
- Subpasta `web/src/features/listings/improvement/` com `ListingDetailPage`, `ImprovementPage`, `FieldComparison`, `wordDiff.ts`, `improvementMessages.ts` e `queries.ts`.
- Diferença por palavra com um algoritmo LCS simples em `wordDiff.ts` (título ≤ 60 caracteres, descrição ≤ 50.000; o diff da descrição é por linha e, dentro da linha alterada, por palavra).
- Componentes novos só se não houver equivalente em `web/src/ui`: `Checkbox` e `Select` acessíveis entram em `web/src/ui`.
- Estado da edição local à página; nada é salvo no servidor antes de "Publicar".

**Justificativa:** mantém a SPA no padrão da ADR-0013 e da SPEC-0025. O diff próprio evita dependência nova para uma função de poucas dezenas de linhas, testada em unidade.

**Desvio do padrão existente:** nenhum.

**Alternativas descartadas:**
- Biblioteca de diff (ex.: `diff`): dependência nova sem necessidade.
- Comparação lado a lado sempre: não cabe no celular; no celular, os blocos "Antes" e "Depois" ficam empilhados por campo.
- Salvar rascunho da edição no servidor: complexidade sem pedido do produto.

**ADRs:** ADR-0002, ADR-0013, ADR-0017.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:**
  - detalhe em < 1 s no ambiente local (E2E com o simulador);
  - diff de uma descrição de 5.000 caracteres em < 50 ms (UT-02);
  - o bundle da SPA cresce no máximo 30 KB gzip (saída do build no G2).
- **Segurança:**
  - nenhum texto do anúncio é renderizado como HTML (só texto; o React escapa) — UT-03;
  - a publicação envia só os campos marcados, com os valores exibidos — UT-03.
- **Privacidade e dados pessoais:** N/A — exibe só dados do anúncio do próprio workspace.
- **Disponibilidade e resiliência:**
  - erro de rede ou `ML_UNAVAILABLE` mostra mensagem com "Tentar de novo";
  - falha da geração mostra o motivo e "Gerar de novo" (quando há cota);
  - publicação parcial mostra o que foi e o que não foi publicado, sem esconder o resultado.
- **Acessibilidade (UI):**
  - WCAG 2.2 AA no detalhe e na melhoria, nas duas larguras;
  - axe sem violações sérias ou críticas;
  - a diferença não depende só de cor: trechos com `<ins>`/`<del>` e rótulo para leitor de tela ("Adicionado", "Removido");
  - estado "Gerando sugestão…" com `aria-live="polite"`;
  - caixas de seleção com rótulo do campo; campo bloqueado com o motivo associado (`aria-describedby`);
  - verificação: E2E-01/E2E-02 (axe) e UT-03.
- **Custo:** N/A — a IA é a simulada fora do go-live; a tela mostra a cota.

## 6. Artefato A — Contrato
**Interface:** telas `/anuncios/$itemId` e `/anuncios/$itemId/melhoria`; link "Melhorar" em `/anuncios`.

```text
Todas exigem sessão (sem sessão -> /entrar).

/anuncios (SPEC-0025, alterada)
  cada anúncio ativo ganha o link "Melhorar" (aria-label "Melhorar o anúncio {título}") -> /anuncios/{itemId}
  o título do anúncio passa a levar ao detalhe

/anuncios/$itemId
  carregando: esqueleto; GET details + GET quota (+ GET latest para saber se há melhoria em andamento ou pronta)
  cabeçalho: miniatura, título, código MLB, preço, estado, "Ver no Mercado Livre"
  "Atributos": lista nome: valor; obrigatório vazio -> selo âmbar "Obrigatório — falta preencher"
  "Descrição": texto com quebras de linha | "Sem descrição"
  título travado: aviso com o motivo (mensagens abaixo)
  botão primário "Melhorar com IA" -> POST improvements -> /anuncios/{itemId}/melhoria
    desabilitado quando: anúncio não ativo ("Só anúncios ativos podem ser melhorados."), conta para reconectar
    ("Reconecte a conta para melhorar este anúncio." + link "Ir para Contas"), cota esgotada
  há melhoria ready ou generating: link "Ver sugestão" no lugar do botão
  texto da cota: "{remaining} de {limit} melhorias este mês"; esgotada: "Você usou as {limit} melhorias deste mês.
    Elas renovam em {data dd/mm}."
  erros: 404 -> "Anúncio não encontrado." + "Voltar para Anúncios"; 502 -> "O Mercado Livre não respondeu. Tente de
    novo em alguns minutos." + "Tentar de novo"

/anuncios/$itemId/melhoria
  generating: "Gerando sugestão…" (aria-live), indicador indeterminado; polling a cada 2 s;
    após 2 min: "Está demorando mais que o normal." + "Continuar esperando"
  failed: mensagem do failureCode + "Gerar de novo" (se houver cota) + "Voltar ao anúncio"
  ready: um bloco por campo (Título, Descrição, cada atributo sugerido):
    caixa "Publicar este campo" (marcada se suggested e grounded; desmarcada se grounded=false)
    "Antes" e "Depois" (desktop lado a lado; celular empilhado), com a diferença destacada
    "Depois" editável: título (contador {n}/{max}), descrição (área de texto, contador), atributo (seletor de valores
      permitidos, ou número + unidade, ou texto)
    grounded=false: aviso âmbar "Confira antes de publicar: “{termos}” não aparece no seu anúncio."
    blockedReason/discardedReason: campo desabilitado com a mensagem do código
  rodapé: "Publicar no Mercado Livre" (primário; desabilitado sem campo marcado ou com campo inválido),
          "Descartar" (diálogo de confirmação "Descartar esta sugestão? A melhoria usada não volta para a cota."),
          "Gerar de novo" (descarta a atual e pede outra; desabilitado sem cota)
  publicar: POST apply { fields com os valores finais dos campos marcados }; botão em "Publicando…"
  resultado (200): por campo: "Publicado" (verde, com ícone e texto) | "Recusado: {mensagem do code}" |
    "Não enviado: {mensagem do code}"; título geral: "Melhoria publicada no Mercado Livre." | "Parte da melhoria
    foi publicada." | "Nada foi publicado."; link "Ver anúncio" (detalhe atualizado) e "Ver no Mercado Livre"
  applied/partially_applied (latest com result): mostra o resultado direto

Mensagens por code:
  IMPROVEMENT_LIMIT_REACHED   "Você usou as {limit} melhorias deste mês. Elas renovam em {data}."
  IMPROVEMENT_IN_PROGRESS     "Já estamos gerando uma sugestão para este anúncio." (vai para a melhoria)
  LISTING_NOT_ACTIVE          "Só anúncios ativos podem ser melhorados."
  ACCOUNT_REAUTHORIZATION_REQUIRED "Reconecte a conta para continuar." + "Ir para Contas"
  IMPROVEMENT_NOT_READY       "A sugestão ainda não está pronta."
  IMPROVEMENT_ALREADY_APPLIED "Esta melhoria já foi publicada."
  VALIDATION_FAILED           mensagem por campo (abaixo)
  ML_UNAVAILABLE              "O Mercado Livre não respondeu. Nada foi publicado. Tente de novo em alguns minutos."
  AI_REFUSED                  "A IA não conseguiu gerar uma sugestão para este anúncio."
  AI_UNAVAILABLE, AI_INVALID_OUTPUT "Não foi possível gerar a sugestão agora. Tente de novo."
  LISTING_NOT_EDITABLE        "Este anúncio não pode ser editado agora no Mercado Livre."
  TITLE_LOCKED_SOLD           "O Mercado Livre não permite mudar o título de anúncio com vendas."
  TITLE_LOCKED_CATALOG        "O Mercado Livre não permite mudar o título de anúncio de catálogo."
  TITLE_LOCKED_USER_PRODUCT   "O título deste anúncio é definido pelo produto no Mercado Livre."
  TITLE_EMPTY / TITLE_TOO_LONG "O título não pode ficar vazio." / "O título passa de {max} caracteres."
  DESCRIPTION_INVALID         "A descrição só pode ter texto simples (sem HTML nem emojis)."
  DESCRIPTION_CONTACT         "A descrição não pode ter e-mail, telefone ou links."
  DESCRIPTION_TOO_LONG        "A descrição passa de {max} caracteres."
  ATTRIBUTE_NOT_EDITABLE      "Este atributo não pode ser alterado."
  ATTRIBUTE_VALUE_NOT_ALLOWED "Valor não aceito pelo Mercado Livre para este atributo."
  ATTRIBUTE_NOT_GROUNDED      "A IA não achou esse valor no seu anúncio."
  ATTRIBUTE_UNKNOWN           "Atributo não existe nesta categoria."
  FIELD_NOT_UPDATABLE         "O Mercado Livre não permitiu mudar este campo."
  ITEM_NOT_ACTIVE             "O anúncio não está ativo no Mercado Livre."
  ML_CONFLICT                 "O anúncio estava sendo alterado no Mercado Livre. Tente de novo."
  ML_FORBIDDEN                "O Vitrine não tem permissão para alterar este anúncio. Reconecte a conta."
  ML_REJECTED                 "O Mercado Livre recusou: {message}"
  desconhecido                "Não foi possível concluir. Tente de novo."
```

**Design:** Telas no Stitch (projeto `projects/6635450613032213900`, design system `assets/15895198957828239862`): comparação "Melhorar anúncio" mobile `f8865dd413234a22beb7c46a561f8895`; o detalhe do anúncio e as versões desktop seguem o mesmo design system e as telas de Anúncios da SPEC-0025 (`f43e752c06254300b0b9647c34239144`) (projeto "Vitrine", `projects/6635450613032213900`, sistema de design `assets/15895198957828239862`): detalhe do anúncio (mobile e desktop), melhoria com comparação (mobile e desktop) e resultado da publicação (mobile). Sem desenho até lá, segue os componentes da SPEC-0025.

**Arquivos/módulos afetados:** ver `touches`.
- `web/src/features/listings/improvement/**` (páginas, componentes, diff, mensagens, queries) e o link em `web/src/features/listings/ListingsList.tsx`.
- `web/src/router.tsx` (rotas novas), `web/src/ui/{Checkbox,Select}.tsx`.
- `web/e2e/aprovar-melhoria.spec.ts` e helpers em `web/e2e/support/{ml-simulator,vitrine}.ts`.
- `docs/specs/sdd-config.yml` (jornada `aprovar-melhoria`, editada pelo Architect).

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Pedir, comparar, editar e publicar | anúncio ativo sem vendas | sugestão, edição, publicação; "Publicado" por campo; anúncio atualizado no detalhe e na lista | E2E-01 |
| Detalhe do anúncio | atributos, descrição, título travado | obrigatório faltando em destaque; "Sem descrição"; motivo do bloqueio | UT-06, E2E-01 |
| Link "Melhorar" | lista de Anúncios (cartões e tabela) | só em ativos; leva ao detalhe | UT-06, E2E-01 |
| Gerando | `generating` | "Gerando sugestão…", polling de 2 s, para no estado final; aviso após 2 min | UT-04, E2E-01 |
| Diferença | antes × depois | trechos adicionados e removidos com `<ins>`/`<del>` e rótulo | UT-02, E2E-01 |
| Seleção e edição | grounded, não grounded, bloqueado, descartado | marcado; desmarcado com aviso; desabilitado com motivo; contador e limite | UT-03 |
| Título bloqueado | anúncio com vendas (ou catálogo) | título desabilitado com "O Mercado Livre não permite mudar o título de anúncio com vendas."; o resto publica | UT-03, E2E-02 |
| Publicação parcial | o ML recusa a descrição | título "Publicado", descrição "Recusado: …" | UT-05, E2E-03 |
| Cota | `remaining`, esgotada | "12 de 20 melhorias este mês"; botão desabilitado com a data de renovação | UT-05, E2E-04 |
| Descartar | diálogo confirmado | volta ao detalhe; anúncio inalterado; "Melhorar com IA" volta a aparecer | UT-03, E2E-05 |
| Gerar de novo | sugestão pronta, com cota | descarta e gera outra | UT-03 |
| Mensagens | cada `code` | texto do §6; desconhecido com mensagem genérica | UT-01 |
| Erros de rede e do ML | 502, falha de geração | mensagem com "Tentar de novo"/"Gerar de novo" | UT-01, UT-04 |
| Acessibilidade | detalhe, melhoria e resultado, 2 larguras | axe sem violações sérias/críticas; diff e estados com texto | UT-03, E2E-01, E2E-02 |
| Contrato | cliente orval | telas compilam contra os tipos gerados; sem drift | CT-01 |
| Link direto e recarga | `/anuncios/{itemId}` e `/melhoria` abertos direto | a Api serve o `index.html`; API inexistente continua 404 JSON | IT-01 |
| Jornada anterior | `conectar-conta` | E2E da SPEC-0025 continua passando com o link novo | CH-01 |

## 7. Artefato B — Plano de Testes (TDD)
<!-- Todo teste tem um ID e é escrito ANTES da implementação (Red). No código, cada teste carrega a tag `SPEC-0038:<ID>` — o G1 (`spec_graph.py verify`) verifica. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem testes: "N/A — motivo" SEM ID. Teste que protege comportamento existente e passa antes da mudança: marque `(guarda: passa antes da mudança)`. Estratégia completa: references/testing.md da skill. -->

### 7.1 Testes de Caracterização
- **CH-01** — Dado o E2E da jornada `conectar-conta` (SPEC-0025, E2E 01 a 05), quando roda depois do link "Melhorar" e do título clicável na lista, então continua passando (guarda: passa antes da mudança).

### 7.2 Testes Unitários
<!-- vitest + @testing-library/react (happy-dom), como web/src/features/listings/ListingsList.test.tsx, com o cliente simulado de web/src/features/test-support/fakeApi.tsx. -->
- **UT-01** — Dado cada `code` do §6 (dos contratos SPEC-0035@1 e SPEC-0036@1, inclusive os `failureCode` e os códigos por campo) e um desconhecido, quando mapeado, então devolve a mensagem do §6, com `{limit}`, `{data}`, `{max}` e `{message}` preenchidos; o desconhecido recebe a mensagem genérica.
- **UT-02** — Dado `wordDiff`, quando compara "Fone de ouvido bluetooth" com "Fone de ouvido Bluetooth sem fio preto", então marca as palavras removidas e adicionadas na ordem; textos iguais não têm marcação; a descrição é comparada por linha e, na linha alterada, por palavra; uma descrição de 5.000 caracteres é comparada em < 50 ms.
- **UT-03** — Dado a página da melhoria com um `latest` `ready` simulado (título travado por vendas, descrição com `grounded=false`, `COLOR` sugerido, `MODEL` descartado), então:
  - o título aparece desabilitado com a mensagem de `TITLE_LOCKED_SOLD`, a descrição vem desmarcada com o aviso e os termos, `COLOR` vem marcado e `MODEL` desabilitado com o motivo;
  - editar a descrição atualiza o contador, e o campo não aceita passar do máximo;
  - marcar a descrição e publicar envia `apply` só com `description` (o valor editado) e `attributes: [COLOR]`;
  - sem campo marcado, "Publicar no Mercado Livre" fica desabilitado;
  - texto com `<b>` aparece como texto, não como HTML;
  - "Descartar" abre o diálogo com foco no botão de cancelar, Esc fecha e devolve o foco, e confirmar chama `discard` e volta ao detalhe;
  - "Gerar de novo" chama `discard` e depois `POST improvements`.
- **UT-04** — Dado o polling com relógio falso, quando o `latest` está `generating`, então consulta a cada 2 s; ao virar `ready` ou `failed`, para; depois de 2 min, mostra "Está demorando mais que o normal." com "Continuar esperando", que retoma o polling. `failed` com `AI_REFUSED` mostra a mensagem e "Gerar de novo".
- **UT-05** — Dado resultados de `apply` simulados, então: tudo `applied` mostra "Melhoria publicada no Mercado Livre." e "Publicado" em cada campo; título `applied` e descrição `rejected` com `DESCRIPTION_INVALID` mostra "Parte da melhoria foi publicada." e "Recusado: A descrição só pode ter texto simples (sem HTML nem emojis)."; um `not_attempted` mostra "Não enviado: …". Dado `quota` com `remaining` 12 de 20, o detalhe mostra "12 de 20 melhorias este mês"; com 0, o botão fica desabilitado com "Você usou as 20 melhorias deste mês. Elas renovam em 01/12.".
- **UT-06** — Dado o detalhe com `details` simulado e a lista de Anúncios, então:
  - obrigatório vazio aparece com "Obrigatório — falta preencher"; sem descrição, "Sem descrição"; título travado, o motivo;
  - anúncio pausado não tem "Melhorar com IA" habilitado e mostra "Só anúncios ativos podem ser melhorados.";
  - na lista, só anúncios ativos têm o link "Melhorar", com `aria-label` com o título, nos cartões e na tabela.

### 7.3 Testes de Integração
- **IT-01** — Dado a Api com um `index.html` de teste no web root (padrão de `tests/Vitrine.IntegrationTests/Security/SpaFallbackTests.cs`), quando o navegador recarrega `/anuncios/MLB9000000003` e `/anuncios/MLB9000000003/melhoria` (link direto), então recebe o `index.html` com a CSP da SPEC-0008, e `/api/v1/listings/MLB9000000003/inexistente` continua respondendo 404 JSON, sem o `index.html` (guarda: passa antes da mudança). Os endpoints consumidos já são cobertos pelos IT das SPEC-0035 e SPEC-0036.

### 7.4 Testes de Contrato
- **CT-01** — Dado o cliente orval regenerado a partir do OpenAPI das SPEC-0035/0036, quando o `tsc --noEmit` e o drift check do CI (SPEC-0011) rodam, então as telas compilam contra os tipos gerados e o cliente versionado está em dia. É o lado consumidor dos contratos SPEC-0035@1 e SPEC-0036@1.

### 7.5 Testes E2E
<!-- Playwright, projetos desktop e mobile, AppHost com o simulador do ML (SPEC-0034) e a IA simulada. Cada teste cria o próprio vendedor (POST /__sim/sellers) e o próprio usuário; miniaturas de *.mlstatic.com interceptadas, como na SPEC-0025. -->
- **E2E-01** — Dado um vendedor novo no simulador e o usuário com a conta conectada, quando o usuário:
  1. abre Anúncios e clica em "Melhorar" no 3º anúncio ativo (sem vendas, sem catálogo);
  2. vê no detalhe o atributo "Cor" com "Obrigatório — falta preencher" e clica em "Melhorar com IA";
  3. espera "Gerando sugestão…" virar a comparação;
  4. confere a diferença no título, edita o título sugerido e deixa "Cor" (Preto) e a descrição marcados;
  5. clica em "Publicar no Mercado Livre";

  então vê "Melhoria publicada no Mercado Livre." com "Publicado" em cada campo; o detalhe mostra o título editado e "Cor: Preto" sem o selo de obrigatório; a lista de Anúncios mostra o título novo; o simulador registrou as escritas (`/__sim/.../writes`); a cota mostra uma melhoria a menos; e o axe não acha violações sérias ou críticas no detalhe, na comparação e no resultado. [jornada: aprovar-melhoria] @smoke
- **E2E-02** — Dado o 1º anúncio ativo (com vendas), quando o usuário pede a melhoria, então o título aparece desabilitado com "O Mercado Livre não permite mudar o título de anúncio com vendas."; ao publicar os demais campos, o título no simulador não muda e os outros campos são publicados. O axe roda na comparação com o campo bloqueado.
- **E2E-03** — Dado o simulador com `fail-next-write` na descrição (400 `item.description.type.invalid`), quando o usuário publica título e descrição, então vê "Parte da melhoria foi publicada.", o título "Publicado" e a descrição "Recusado: A descrição só pode ter texto simples (sem HTML nem emojis)."; o detalhe mostra o título novo e a descrição antiga.
- **E2E-04** — Dado o workspace que já usou as 20 melhorias do mês (consumidas pela API com a IA simulada, uma por anúncio, esperando cada uma ficar pronta), quando o usuário abre o detalhe de um anúncio, então vê "Você usou as 20 melhorias deste mês. Elas renovam em {data}." e "Melhorar com IA" desabilitado.
- **E2E-05** — Dado uma sugestão pronta, quando o usuário clica em "Descartar" e confirma, então volta ao detalhe, o anúncio no simulador continua igual (nenhuma escrita registrada) e "Melhorar com IA" volta a aparecer.

### 7.6 Outros
- Acessibilidade: axe no E2E-01 e E2E-02 (critério: zero sérias/críticas).
- Tamanho do bundle: saída do `npm run build` registrada no G2, com crescimento ≤ 30 KB gzip.

**Dublês e dados de teste:**
- vendedores criados por teste no simulador, com a massa da SPEC-0034 (1º ativo com vendas, 2º de catálogo, `COLOR` vazio derivável da descrição);
- helpers novos em `support/ml-simulator.ts`: `setItemState` (vendas, catálogo, descrição), `failNextWrite` e `itemWrites`;
- usuários com e-mail único (helpers de `support/vitrine.ts`);
- nos testes de componente, dados no formato dos contratos e o cliente simulado (`fakeApi.tsx`).

**Ambiente de execução:**
- UT com vitest (local e CI);
- E2E contra o AppHost com o simulador e `Ai__ListingImprover__Provider=simulated` (local e CI);
- `@smoke` (E2E-01) no ambiente local prod-like (perfil `ml-simulator`, `AI_ALLOW_SIMULATED=true`) no G6.

## 8. Plano de Rollout
- **Estratégia:** deploy direto. Com a IA simulada até o go-live; o provedor real é ligado por configuração na SPEC-0009, sem mudança de tela.
- **Dados/schema:** N/A.
- **Compatibilidade:** a lista de Anúncios ganha o link "Melhorar" e o título clicável; o E2E da `conectar-conta` segue como guarda (CH-01).
- **Observabilidade:** erros de tela pelo Sentry da SPA (SPEC-0007, quando configurado); métricas de geração e publicação no backend (SPEC-0035/0036).
- **Rollback:** revert do merge. As rotas novas somem; os dados do backend não mudam.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Campos com aviso de grounding vêm marcados? — Não. Vêm desmarcados, com o aviso dos termos; o vendedor marca se o termo for verdadeiro. (Architect, 2026-11-02, a confirmar no H1)
- [x] "Gerar de novo" consome cota? — Sim: é uma geração nova. O botão mostra a cota e fica desabilitado sem ela. (Architect, 2026-11-02, a confirmar no H1)
- [x] Confirmar antes de publicar? — Não há diálogo extra: o botão "Publicar no Mercado Livre" com os campos marcados à vista é a aprovação. Descartar tem confirmação, porque a cota usada não volta. (Architect, 2026-11-02, a confirmar no H1)
- [x] Telas no Stitch? — Geradas pelo Architect antes do H1 (detalhe, comparação e resultado, mobile e desktop); até lá, os componentes da SPEC-0025. (Architect, 2026-11-02, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 0: Caracterização**
- [ ] CH-01 escrito, passando no código atual (lista de anúncios e jornada conectar-conta), commitado antes de qualquer mudança

**Fase 1: Componentes e cliente**
- [ ] Red: escrever UT-01..UT-06 e CT-01 com a tag `SPEC-0038:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: cliente orval regenerado, telas `/anuncios/$itemId` e `/anuncios/$itemId/melhoria` (comparação, seleção, edição, resultado por campo)
- [ ] Refactor mantendo tudo verde

**Fase 2: Jornada aprovar-melhoria**
- [ ] Red: escrever IT-01 e E2E-01..E2E-05 com a tag `SPEC-0038:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: fluxo completo contra a stack com o simulador, E2E-01 com `@smoke`, mobile e desktop
- [ ] Refactor mantendo tudo verde
- [ ] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 12. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência (comando + resultado, SHA, execução de CI, veredito). -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — b44124e (árvore suja) | 2026-11-02 |
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
- [ ] Guia de jornadas e mapa do sistema atualizados quando a entrega muda uma jornada ou um componente (G7)
- [ ] Pendências registradas como novas specs (ou nenhuma)

### Deploy
<!-- ambiente(s), versão/tag, data, estratégia, estado da feature flag, execução do pipeline -->

### Pendências
<!-- specs criadas para o que ficou de fora, ou "Nenhuma" -->

## 15. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0038`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

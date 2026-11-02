---
id: SPEC-0032
title: Melhorar anúncio com IA (aprovar e publicar no Mercado Livre)
tier: epic
type: feature
status: approved
created: 2026-11-02
depends_on: [SPEC-0019]
adrs: [ADR-0001, ADR-0002, ADR-0006, ADR-0010, ADR-0012, ADR-0014, ADR-0015, ADR-0016, ADR-0017]
external: []
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0032 — Melhorar anúncio com IA (aprovar e publicar no Mercado Livre) (Épico)

## 1. Visão
O vendedor:
- abre um anúncio no Vitrine e pede "Melhorar com IA";
- recebe uma sugestão de título, descrição e atributos que faltam, que nunca inventa fatos e só usa valores aceitos pela categoria;
- compara antes e depois, campo por campo, edita, escolhe o que publicar e aprova;
- vê o resultado por campo (publicado, recusado com o motivo, não enviado) e o anúncio atualizado no Vitrine.

É a segunda jornada de produto (`aprovar-melhoria`) e a função central do Vitrine (ADR-0015).

Tecnicamente, o épico entrega:
- leitura detalhada e escrita de anúncios no gateway do ML, com o resultado por campo e as regras do ML numa classe única (`ListingChangeRules`);
- a sugestão no módulo `Listings`, atrás da porta `IListingImprover`, com cota mensal e custo por workspace;
- a publicação aprovada, com auditoria e atualização imediata da cópia local;
- a suíte de avaliação da IA;
- o simulador do ML com detalhe, categorias e edição.

**Sem custo até o go-live:** em dev, CI, E2E e no ambiente local, a IA é um gerador simulado determinístico (ADR-0017). O adaptador real da Anthropic existe e é testado com respostas gravadas, sem chamada paga, mas fica desligado (`Ai:ListingImprover:Provider`, padrão `simulated`). Ele é ligado no go-live (SPEC-0009), junto com o usuário, depois da avaliação real.

## 2. Escopo
**Objetivos (dentro do escopo):**
- Gateway do ML (SPEC-0033): `IMercadoLivreListingDetails` (item, descrição, categoria com cache de 24 h), `IMercadoLivreListingEditor` (título e atributos num PUT, descrição por PUT ou POST, erros com códigos estáveis, 409 com uma nova tentativa, sem retry de escrita no pipeline) e `ListingChangeRules`.
- Simulador do ML (SPEC-0034): detalhe, descrição, categorias sintéticas, edição com as regras do ML, notificação depois da edição, massa com vendas, catálogo e atributos faltando, e controles de falha forçada.
- Sugestão (SPEC-0035): agregado `ListingImprovement`, geração durável, gerador simulado, adaptador Anthropic (Opus 5.5, saída estruturada, cache de prompt), validação pós-IA, grounding, cota de 20 por mês por workspace, uma geração por anúncio por vez, tokens e custo, endpoints de detalhe, pedido, última melhoria e cota.
- Publicação (SPEC-0036): apply com os valores finais revalidados, resultado por campo, cópia local atualizada na hora, evento `ListingImprovementApplied` v1, auditoria e descarte.
- Avaliação (SPEC-0037): dataset versionado de 40 casos, avaliadores determinísticos e por rubrica, CI com o provedor simulado, execução real manual.
- Telas (SPEC-0038): detalhe, comparação, resultado, link "Melhorar" na lista e E2E da jornada `aprovar-melhoria`.
- Correção do ambiente local (SPEC-0039): o Caddy local escuta só em `127.0.0.1`.

**Não-objetivos (fora do escopo):**
- Ligar o provedor real de IA, rodar a avaliação real e definir limites de gasto no console da Anthropic: go-live (SPEC-0009), com o usuário.
- Nota de qualidade do anúncio (`GET /item/{id}/performance`, `score` 0–100, `level_wording` Básica/Satisfatória/Profissional): épico futuro.
- User products: título pelo `family_name` (`PUT /items/{id}/family_name`). A tela bloqueia e explica.
- Fotos, preço, estoque, variações; desfazer uma publicação; publicação em lote.
- Cota por plano de assinatura (épico de assinatura); aqui é um número por configuração.
- Responder perguntas (épico próprio).

## 3. Arquitetura Alvo
**Contexto:** projeto existente, com os módulos `MercadoLivre` e `Listings` entregues pelo épico SPEC-0019 (ADR-0001: monólito modular; ADR-0014: Wolverine com outbox; ADR-0016: simulador do ML). A ADR-0015 já decide o provedor de IA (Claude, porta `IListingImprover`, SDK `Anthropic` só no `Infrastructure`, suíte de avaliação). Nada muda na arquitetura: o épico amplia os dois módulos e acrescenta um adaptador de IA. Referências de padrão: `src/Modules/MercadoLivre/Vitrine.MercadoLivre.Infrastructure/Api/MercadoLivreItemsGateway.cs`, `src/Modules/Listings/Vitrine.Listings.Presentation/ListingsEndpoints.cs`, `src/Modules/Listings/Vitrine.Listings.Infrastructure/Messaging/ItemChangedHandler.cs`, `tools/MercadoLivreSimulator/`, `web/src/features/listings/`.

```text
SPA (/anuncios, /anuncios/$itemId, /anuncios/$itemId/melhoria)
  │ cookie de sessão, antiforgery
  ▼
Api ── Listings.Presentation  (/api/v1/listings/{itemId}/details | /improvements | /improvements/latest |
     │                         /improvements/{id}/apply | /improvements/{id}/discard; /listings/improvements/quota)
     │      └ Application: pedir (cota, exclusividade), ListingImprovementGenerator (validação + grounding),
     │                     aprovar/publicar, descartar
     │      └ Infrastructure: listing_improvements + auditoria (schema listings), comando durável
     │                        GenerateListingImprovement, adaptadores IListingImprover:
     │                          simulated (padrão) | anthropic (SDK oficial, desligado até o go-live)
     │      └ IntegrationEvents: ListingImprovementApplied v1
     └─ MercadoLivre.PublicApi: IMercadoLivreItems v1 (inalterado), IMercadoLivreListingDetails,
                                IMercadoLivreListingEditor, ListingChangeRules
              └ Infrastructure: gateways sobre o cliente mercadolivre-api (sem retry de escrita)

ML (real, ou simulador em dev/E2E/local) ◀── gateway       Anthropic ◀── adaptador (só com Provider=anthropic)
PUT no ML ──▶ notificação items ──▶ ItemChangedHandler (eco inofensivo: regra lastUpdated >=)
```

Contratos entre as filhas:
- **SPEC-0033@1:** `IMercadoLivreListingDetails`, `IMercadoLivreListingEditor`, `ListingChangeRules`, `MercadoLivreItemNotFoundException` e as fixtures de `tests/Fixtures/MercadoLivre/`.
- **SPEC-0034@1:** contrato do simulador (subconjunto da API do ML do contrato SPEC-0033, mais `/__sim/*`).
- **SPEC-0035@1:** endpoints `details`, `improvements`, `latest`, `quota`; porta `IListingImprover`, `ListingImprovementGenerator`, `AddListingImprover` e configuração `Ai:*`.
- **SPEC-0036@1:** endpoints `apply` e `discard`, `result` do `latest`, evento `ListingImprovementApplied` v1.

**Decisões (ADRs):**
- Aplicadas: ADR-0001 (módulos e `PublicApi`), ADR-0002 (testes; eval manual), ADR-0006 (chave da Anthropic como segredo; retenção até desconectar), ADR-0010 (idempotência, sem retry de escrita), ADR-0012 (custo por workspace, cota de 20), ADR-0014 (comando durável), ADR-0015 (Claude Opus 5.5, porta, saída estruturada), ADR-0016 (simulador do ML).
- Nova: **ADR-0017**, IA simulada em desenvolvimento, E2E e ambiente local.

**Regras de arquitetura a garantir (G3):**
- Só `Vitrine.Listings.Infrastructure` referencia o pacote `Anthropic` — SPEC-0035:UT-07.
- `MercadoLivre.PublicApi` continua só com BCL e sem tipo com token — SPEC-0033:UT-05.
- `Listings` só usa `MercadoLivre.PublicApi`/`IntegrationEvents` e `Identity.PublicApi` (regras da SPEC-0022, ampliadas) — SPEC-0035:UT-07.
- O host recusa subir com `Provider=anthropic` sem `Ai:Anthropic:ApiKey`, e com `simulated` em `Production` sem `AllowSimulated` — SPEC-0035:UT-06.
- CI, E2E e ambiente local sempre com `simulated`; nenhum arquivo versionado com chave — SPEC-0035:IT-09.
- A Api não referencia o simulador (ADR-0016, teste existente da SPEC-0024).

## 4. Decomposição
| Spec | Título | Tier | Tipo | Tamanho | Depende de | Consome contrato de |
|---|---|---|---|---|---|---|
| SPEC-0033 | Leitura detalhada e edição de anúncios no gateway do Mercado Livre | full | feature | M | — | SPEC-0020@1, SPEC-0021@1 |
| SPEC-0034 | Simulador do ML: detalhes, categorias e edição de anúncios | full | feature | M | SPEC-0024, SPEC-0033 | SPEC-0033@1 |
| SPEC-0035 | Sugestão de melhoria de anúncio com IA (geração, cota e custo) | full | feature | M | SPEC-0033 | SPEC-0033@1 |
| SPEC-0036 | Aprovar e publicar a melhoria no Mercado Livre | full | feature | M | SPEC-0035 | SPEC-0033@1 |
| SPEC-0037 | Suíte de avaliação da melhoria de anúncio | full | feature | M | SPEC-0035 | SPEC-0035@1 |
| SPEC-0038 | Telas de detalhe e melhoria de anúncio (jornada aprovar-melhoria) | full | feature | M | SPEC-0034, SPEC-0036 | SPEC-0035@1, SPEC-0036@1 |
| SPEC-0039 | Ambiente local prod-like escuta na internet | lite | fix | S | — | — |

## 5. Estratégia de Entrega
- **Ambientes:**
  - desenvolvimento (`aspire run`, com o simulador do ML e a IA simulada) e CI (E2E com os dois simulados);
  - ambiente local prod-like `https://vitrine.localhost`, só em `127.0.0.1` depois da SPEC-0039, com o perfil `ml-simulator` e `AI_ALLOW_SIMULATED=true`, para o teste manual do usuário;
  - staging e produção só existem com a SPEC-0009.
- **Entrega por onda** (ondas calculadas por `spec_graph.py waves`; ordem sugerida):
  1. SPEC-0039 e SPEC-0033 (correção do ambiente local; gateway e contrato do ML, com fixtures);
  2. SPEC-0034 e SPEC-0035 (simulador; sugestão com a IA simulada);
  3. SPEC-0036 e SPEC-0037 (publicação; avaliação no CI com o provedor simulado);
  4. SPEC-0038 (telas e E2E da jornada).

  Cada onda entra na `main` pelo PR com CI verde e roda no ambiente local prod-like (G6). Os `touches` foram separados para não haver conflito dentro da mesma onda.
- **Feature flags:**
  - `Ai:ListingImprover:Provider` — `simulated` (padrão) ou `anthropic` — liga no go-live (SPEC-0009), com o usuário, depois de: avaliação real aprovada (100% nos determinísticos, ≥ 90% na rubrica), limite de gasto no console da Anthropic e chave no secret do ambiente. Não é removida: continua escolhendo o adaptador (ADR-0015).
  - `Ai:ListingImprover:AllowSimulated` — permite `simulated` em `Production` — só no ambiente local; staging e produção nunca a definem.
- **Rollback:** revert do merge da onda. As migrações (`listing_improvements` e auditoria) são só de criação. Publicações já feitas no ML não são desfeitas pelo revert; o texto original fica guardado na melhoria.
- **Métricas de sucesso pós-release** (no go-live, SPEC-0009; antes disso, no dashboard do Aspire), por 4 semanas:
  - taxa de melhorias geradas que viram publicação (`improvements{result=ready}` × `improvement_applications{outcome=applied|partially_applied}`), alvo ≥ 50%;
  - taxa de campos recusados pelo ML (`mercadolivre.listing_edits{result=rejected}`), alvo < 5%;
  - custo de IA por melhoria (`improvement_cost_usd`), alvo ≤ US$ 0,08 (ADR-0012 estima US$ 0,06);
  - recusas e falhas da IA (`failed_ai_*`), alvo < 2%.
- **Pendências do go-live (SPEC-0009, com o usuário):**
  - confirmar as regras do ML (título com vendas e catálogo, descrição, atributos, códigos de erro, escopo `write`) com usuário de teste real;
  - rodar a avaliação real (SPEC-0037) e escolher effort e fallback de recusa;
  - ligar o provedor real e definir limites de gasto no console da Anthropic (ADR-0012: US$ 500/mês, ajustável);
  - dataset v2 com anúncios anonimizados do usuário;
  - nota de qualidade `/item/{id}/performance` (épico futuro) e user products/`family_name`.

## 6. Riscos & Mitigações
- **As regras do ML divergem do que foi pesquisado** (a documentação oficial respondeu 403 em 2026-11-02; os fatos vêm de trechos de busca).
  - A regra fica num lugar só (`ListingChangeRules`), o erro do ML é traduzido por uma tabela, e campo recusado aparece com o motivo, sem quebrar o resto.
  - Fixtures sintéticas marcadas como tais; conferência com usuário de teste real no go-live.
- **Escrita duplicada ou sem aprovação** (o erro mais caro, ADR-0002).
  - Nenhuma escrita retentada pelo pipeline (SPEC-0033:IT-07); apply exclusivo (SPEC-0036:IT-03); só os campos aprovados vão no corpo (SPEC-0038:UT-03).
- **A IA inventa fatos ou usa valores não aceitos.**
  - Validação pós-IA com a regra do editor e verificação de grounding no servidor (SPEC-0035:IT-08); campo sem origem desmarcado e com aviso na tela; avaliação real com 100% de grounding antes de ligar.
- **Custo de IA sem controle.** Provedor simulado até o go-live; cota de 20 por workspace; custo por melhoria registrado; limite de gasto na Anthropic no go-live.
- **O simulador diverge do ML real.** Ele imita o ML com regras próprias (não reaproveita `ListingChangeRules`), e o E2E pega divergências entre os dois; conferência real no go-live.
- **SPEC-0035 é a maior filha** (agregado, dois adaptadores, cota, quatro endpoints). Se o G1 mostrar que passa de M, o Architect separa o endpoint de detalhe e a configuração do provedor numa filha própria, sem mudar o contrato.
- **Ambiente local exposto na internet** (achado do Architect em 2026-11-02). Corrigido na 1ª onda (SPEC-0039), antes de liberar a IA simulada no ambiente local.

## 7. Critérios de Aceite do Épico
- [ ] O vendedor pede a melhoria, compara antes e depois, edita, publica e vê o anúncio atualizado, no celular e no desktop — SPEC-0038:E2E-01.
- [ ] Título bloqueado com vendas ou catálogo: não é sugerido nem enviado, e a tela explica — SPEC-0038:E2E-02, SPEC-0033:IT-04, SPEC-0034:IT-02.
- [ ] Descrição inválida (HTML, emoji, contato, acima do limite) é recusada antes de chamar o ML — SPEC-0033:IT-04, SPEC-0035:IT-08.
- [ ] Cota mensal por workspace: o pedido além do limite é recusado e a tela mostra a renovação — SPEC-0035:IT-03, SPEC-0038:E2E-04.
- [ ] Nenhuma chamada paga sem a flag: dev, CI, E2E e local usam a IA simulada, e o host não sobe com o provedor real sem chave — SPEC-0035:UT-06, SPEC-0035:IT-09, SPEC-0037:UT-09.
- [ ] A sugestão não inventa fatos e só usa valores permitidos da categoria — SPEC-0035:UT-03, SPEC-0035:IT-08, SPEC-0037:IT-01, SPEC-0037:IT-03.
- [ ] A publicação parcial mostra o resultado por campo — SPEC-0036:IT-02, SPEC-0038:E2E-03.
- [ ] Um workspace não vê, não pede nem publica melhoria de anúncio de outro — SPEC-0035:IT-02, SPEC-0036:IT-03.
- [ ] O ambiente local prod-like não responde pelo IP público — SPEC-0039:IT-01.

## 8. Questões em Aberto
- [x] O provedor real de IA entra neste épico? — Entra o adaptador, testado com respostas gravadas, desligado por configuração. Ligar é do go-live (SPEC-0009), com o usuário, depois da avaliação real e do limite de gasto (ADR-0017). (Architect, 2026-11-02, a confirmar no H1)
- [x] Quantas melhorias por workspace? — 20 por mês civil, configurável (ADR-0012: teto de 20 por teste grátis). O limite por plano vem do épico de assinatura. (Architect, 2026-11-02, a confirmar no H1)
- [x] Quem pede, aprova e publica? — Qualquer membro do workspace, como a sincronização; hoje só existe `Owner`. (Architect, 2026-11-02, a confirmar no H1)
- [x] As regras do ML estão confirmadas? — Não. Verificadas por trechos de busca em 2026-11-02 (a documentação oficial devolveu 403). Precisam ser confirmadas com usuário de teste real no go-live (SPEC-0009). (Architect, 2026-11-02, a confirmar no H1)
- [x] User products e anúncios de catálogo? — O título é bloqueado e a tela explica; descrição e atributos seguem as regras normais. A edição pelo `family_name` fica para depois. (Architect, 2026-11-02, a confirmar no H1)
- [x] A nota de qualidade do ML entra? — Não: épico futuro, para medir o efeito da melhoria. (Architect, 2026-11-02, a confirmar no H1)

**Referências do ML (pesquisa de 2026-11-02, só trechos de busca; a documentação oficial devolveu 403). Foram verificadas por trecho de busca e precisam ser confirmadas com usuário de teste real no go-live (SPEC-0009):**
- Título: `PUT /items/{id}`; com `sold_quantity > 0` não é modificável (400 provável `field_not_updatable`, "title is not modifiable"; código exato não verificado); em user products vem do `family_name` (≤ 120 caracteres); em catálogo não é editável (inferido); limite em `settings.max_title_length`.
- Descrição: `POST /items/{id}/description` quando não existe; `PUT /items/{id}/description?api_version=2` com `plain_text` quando existe; só texto puro com `\n`; HTML, entidades e emojis dão 400 `item.description.type.invalid`; limite em `settings.max_description_length` (Global Selling: 50.000); sem contato.
- Atributos: `GET /categories/{id}/attributes` com `tags` (`required`, `catalog_required`, `hidden`, `read_only`), `value_type`, `values`, `allowed_units`; escrita por `PUT /items/{id}` com `attributes: [{id, value_id | value_name}]`; não enviar `read_only` nem `hidden`.
- Leitura: `GET /items/{id}` e `GET /items/{id}/description`; nota de qualidade em `GET /item/{id}/performance` (fora do MVP).
- Erros: só item `active` é editável; 409 de *optimistic locking* pede nova tentativa depois de alguns segundos; 429 sem limite publicado; escopo `write` necessário.
- Notificação `items` dispara em qualquer mudança, inclusive (inferido) no PUT do próprio app; a cópia local relê com o token e tem a regra `lastUpdated >=`, então o eco é inofensivo.

## 9. Aprovação (H1)
Uma aprovação humana cobre o épico e as specs filhas apresentadas junto com ele. Registrada no frontmatter (`approved_by`, `approved_at`) do épico e de cada filha.

## 10. Registro de Impedimentos
<!-- Problemas que envolvem várias filhas (conflito de integração da onda, CI quebrado, decisão transversal). Impedimento que trava uma filha específica vai na própria filha. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 11. Relatório de Entrega
<!-- Preenchido ao fechar o épico: o que foi entregue, como (ondas e deploys com versão/data), resultado dos critérios de aceite com os testes que os provam, métricas pós-release, pendências como novas specs. `spec_graph.py report SPEC-0032` ajuda a montar. -->

## 12. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

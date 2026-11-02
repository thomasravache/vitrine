---
id: SPEC-0037
title: Suíte de avaliação da melhoria de anúncio
tier: full
type: feature
user_facing: false
status: approved
created: 2026-11-02
parent: SPEC-0032
depends_on: [SPEC-0035]
consumes_contract: [SPEC-0035@1]
contract_version: 1
# breaking: "o que quebra para consumidores e como migrar"   # só se houver quebra; vai para o PR e pede `!` no título
touches: [tests/Vitrine.Evals/**, tests/Vitrine.UnitTests/Evals/**, tests/Vitrine.UnitTests/Vitrine.UnitTests.csproj, tests/Vitrine.UnitTests/packages.lock.json, tests/Vitrine.IntegrationTests/Evals/**, tests/Vitrine.IntegrationTests/Vitrine.IntegrationTests.csproj, tests/Vitrine.IntegrationTests/packages.lock.json, Vitrine.slnx, docs/runbooks/avaliacao-ia.md]
adrs: [ADR-0002, ADR-0012, ADR-0015, ADR-0017]
external: []
size: M
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0037 — Suíte de avaliação da melhoria de anúncio

<!-- type: feature | fix | refactor | migration | foundation. user_facing: true quando a mudança altera uma jornada do usuário (UI ou API pública) — exige teste E2E. Substitua todos os marcadores com chaves duplas: o G0 (`spec_graph.py validate`) reprova a spec enquanto restar algum. As seções de Checklist em diante são preenchidas depois da aprovação, sem marcadores. -->

## 1. Visão Geral
Cria a suíte de avaliação (eval) da melhoria de anúncio, exigida pela ADR-0002 e pela ADR-0015 para qualquer mudança de modelo ou prompt:
- um **dataset versionado** de 40 anúncios sintéticos de várias categorias, com os casos difíceis;
- **avaliadores determinísticos**: limites, texto puro, sem contato/link/emoji, atributos só da lista permitida, grounding por extração e título intocado quando bloqueado;
- um **avaliador por rubrica com LLM**, opcional;
- uma ferramenta de linha de comando, `tests/Vitrine.Evals`, que roda o mesmo caminho do produto (`ListingImprovementGenerator` + `IListingImprover`, SPEC-0035) e gera um relatório versionado.

No CI, a suíte roda **com o provedor simulado**, sem custo, para validar o harness, o dataset e as regras. A execução **real**, com o Claude, é um comando manual que exige chave e confirmação de custo, e fica para o go-live, junto com o usuário.

## 2. Motivação & Escopo
**Motivação:** a regra inegociável é que a sugestão não invente fatos e respeite as regras do ML. Testes unitários provam as regras do código, mas não a qualidade do modelo. A eval é a prova antes de ligar o provedor real (ADR-0017) e antes de cada troca de modelo, effort ou prompt (ADR-0015). Ela custa dinheiro, então roda manualmente (ADR-0002); o harness, porém, precisa estar provado no CI para não quebrar no dia do go-live.

**Objetivos (dentro do escopo):**
- Projeto `tests/Vitrine.Evals` (aplicação de console .NET, no `Vitrine.slnx`, com as regras de build do repositório), que referencia `Vitrine.Listings.Application`/`Infrastructure` e registra os adaptadores por `AddListingImprover` (SPEC-0035), com a mesma configuração `Ai:*`.
- Dataset `tests/Vitrine.Evals/Datasets/listing-improvement/v1/cases.jsonl`: 40 casos sintéticos, em pelo menos 6 categorias, com ao menos 3 casos de cada tipo difícil:
  - anúncio com vendas (título bloqueado);
  - anúncio de catálogo;
  - descrição com contato, link ou emoji;
  - atributo obrigatório sem valor derivável do anúncio;
  - descrição vazia;
  - texto com números e medidas (tentação de inventar).
- Avaliadores determinísticos (§6), implementados à parte do código do produto (oráculo independente).
- Avaliador por rubrica (`--rubric`), com modelo configurável (padrão `claude-sonnet-5-5`, ADR-0015), saída estruturada com nota de 1 a 5 por critério.
- Limiares: 100% em cada avaliador determinístico; ≥ 90% dos casos aprovados na rubrica.
- Relatório JSON + Markdown em `tests/Vitrine.Evals/Reports/`, com provedor, modelo, effort, versão do dataset, commit, resultado por caso e por avaliador, tokens e custo.
- Execução real protegida: exige `--provider anthropic`, a chave e `--yes` depois de mostrar a estimativa de custo.
- Testes do harness no CI (provedor simulado e respostas gravadas da Anthropic por WireMock), sem rede externa.
- Runbook `docs/runbooks/avaliacao-ia.md`: como rodar a avaliação real, ler o relatório e anexá-lo ao PR (ADR-0015).

**Não-objetivos (fora do escopo):**
- Rodar a avaliação real: go-live (SPEC-0009), com o usuário. Também a escolha final de effort (`medium` × `high`) e do fallback de recusa.
- Dataset com anúncios reais anonimizados do usuário: v2, no go-live, quando houver anúncios reais.
- Avaliação de respostas a perguntas (épico "Responder perguntas").
- Agendamento automático da avaliação real.

## 3. Dependências
- **Implementações necessárias:** SPEC-0035, porque a suíte executa o gerador, a validação pós-IA e os adaptadores reais.
- **Contratos consumidos:** SPEC-0035@1 (`IListingImprover`, `ImprovementInput`/`ImprovementDraft`, `ListingImprovementGenerator`, `AddListingImprover` e a configuração `Ai:*`).
- **Pré-requisitos externos:**
  - no CI: nenhum;
  - na execução real (go-live): chave da Anthropic do usuário, com limite de gasto no console.

## 4. Decisão Arquitetural
**Contexto:**
- ADR-0002: eval como nível próprio, manual e evidência no G4 das specs de IA.
- ADR-0015: mudança de modelo ou prompt só com a eval anexada ao PR.
- ADR-0017: CI e E2E sempre com `simulated`.
- O padrão de ferramenta .NET fora de `src/` é o simulador (`tools/MercadoLivreSimulator`, ADR-0016), com build igual ao do repositório.
- Os testes do repositório (xUnit v3, ``[Trait("spec", …)]``) ficam em `tests/Vitrine.UnitTests` e `tests/Vitrine.IntegrationTests`.

**Decisão:**
- `tests/Vitrine.Evals` é uma aplicação de console (não um projeto de teste), para aceitar argumentos e gerar o relatório. A lógica (dataset, avaliadores, executor, relatório) é testada a partir de `Vitrine.UnitTests` e `Vitrine.IntegrationTests`, que referenciam o projeto.
- Avaliadores independentes de `ListingChangeRules` e `GroundingCheck`: se o produto errar a regra, a eval pega.
- Avalia o que o vendedor veria (depois da validação pós-IA) e registra também, à parte, as violações da saída crua do modelo (taxa informativa).

**Justificativa:** o mesmo caminho do produto, com o adaptador escolhido por configuração, garante que a eval mede o que vai para a produção. O CI com o provedor simulado prova o harness sem custo (ADR-0017).

**Desvio do padrão existente:** a pasta `tests/` ganha uma aplicação de console. Coberto por esta spec e pela ADR-0002 (eval como nível próprio).

**Alternativas descartadas:**
- Projeto de teste xUnit que chama a API real quando há chave: ficaria no `dotnet test` e poderia gerar custo por engano.
- Ferramenta de eval externa (Python, promptfoo): outra stack para manter, sem reaproveitar o adaptador .NET.
- Avaliar só com rubrica de LLM: caro e não determinístico; as regras inegociáveis exigem verificação exata.

**ADRs:** ADR-0002, ADR-0012, ADR-0015, ADR-0017.

## 5. Requisitos Não-Funcionais
- **Desempenho e escala:** com o provedor simulado, os 40 casos rodam em < 30 s no CI — IT-01. Na execução real, com concorrência de 4 chamadas, alvo de < 15 min.
- **Segurança:**
  - a chave só vem de variável de ambiente (`Ai__Anthropic__ApiKey`) e nunca aparece no relatório nem no console — UT-09, IT-02;
  - sem `--yes`, a execução real não faz nenhuma chamada — UT-09.
- **Privacidade e dados pessoais:** dataset v1 só com dados sintéticos; o v2 (go-live) só com anúncios anonimizados do próprio usuário, sem dado de comprador.
- **Disponibilidade e resiliência:** caso que falha por indisponibilidade da IA é marcado `error` (não `fail`) e reexecutado uma vez; mais de 10% dos casos em `error` invalida a execução (exit 3).
- **Acessibilidade (UI):** N/A — sem tela.
- **Custo:**
  - CI: zero (`simulated` e WireMock);
  - execução real: 40 casos × ≈ US$ 0,06 (ADR-0012) ≈ US$ 2,40; com rubrica (Sonnet 5.5), mais ≈ US$ 0,50; a ferramenta mostra a estimativa antes e o custo real (pelo `usage`) no relatório.

## 6. Artefato A — Contrato
**Interface:** `dotnet run --project tests/Vitrine.Evals -- [opções]`; formato do dataset e do relatório.

```text
dotnet run --project tests/Vitrine.Evals -- [--provider simulated|anthropic] [--dataset v1] [--rubric]
                                            [--limit N] [--case ID] [--concurrency 4] [--out DIR] [--yes]
  --provider  padrão simulated (sem chave, sem rede)
  anthropic   exige Ai__Anthropic__ApiKey; sem ela -> exit 2 "chave ausente" e nenhuma chamada
              sem --yes -> imprime a estimativa (casos × custo médio) e sai com exit 4, sem chamada
  --rubric    exige anthropic (o juiz é um LLM); modelo em Evals:Rubric:Model (padrão claude-sonnet-5-5)
  --out       padrão tests/Vitrine.Evals/Reports
  exit 0      limiares atingidos | 1 limiar não atingido | 2 configuração | 3 erros > 10% | 4 sem confirmação

Dataset (JSON Lines, um caso por linha; versão na pasta):
  { caseId, tags: ["sold"|"catalog"|"contact_in_description"|"emoji"|"link"|"underivable_required"|"no_description"|
                   "numbers"|...],
    item: { title, description|null, soldQuantity, catalogListing, status, categoryId,
            attributes: [{ id, name, valueId|null, valueName|null }] },
    category: { id, name, maxTitleLength, maxDescriptionLength,
                attributes: [{ id, name, valueType, required, readOnly, hidden, values: [{ id, name }], allowedUnits }] },
    expectations: { titleLocked: bool, mustNotFill: [attributeId] } }

Avaliadores determinísticos (cada um: pass | fail com motivo, por caso):
  LIMITS          título ≤ maxTitleLength; descrição ≤ maxDescriptionLength (ou 50.000)
  PLAIN_TEXT      descrição sem tag ou entidade HTML
  NO_CONTACT      título e descrição sem e-mail, telefone, URL, "whatsapp" e emoji
  ALLOWED_VALUES  atributo sugerido existe na categoria, não é read_only/hidden, valor na lista ou unidade permitida
  GROUNDING       todo número, medida (número + unidade) e termo próprio da sugestão aparece no original
                  (título, descrição, valores de atributos), sem diferenciar maiúsculas e acentos
  TITLE_LOCK      com expectations.titleLocked, nenhum título sugerido
  NO_INVENTED_FILL nenhum atributo de expectations.mustNotFill preenchido
  Limiar: 100% dos casos em cada avaliador (sobre a sugestão final, depois da validação pós-IA)
  Informativo: taxa de violação de cada avaliador na saída crua do modelo

Rubrica (opcional): critérios clareza, português correto, mais informativo que o original, sem exagero ou promessa,
  fidelidade ao anúncio; nota 1–5 cada, por saída estruturada; caso aprovado com todas as notas ≥ 4.
  Limiar: ≥ 90% dos casos aprovados.

Relatório (tests/Vitrine.Evals/Reports/{AAAA-MM-DD}-{provider}-{model}-{dataset}.json e .md):
  { runAt, commit, provider, model, effort, dataset, cases, thresholds,
    evaluators: { NAME: { passed, failed, rate, rawViolationRate } }, rubric: { passed, rate }|null,
    usage: { inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, estimatedCostUsd },
    results: [{ caseId, status: pass|fail|error, failures: [{ evaluator, reason }], usage }] }
  Relatório do provedor simulated não é versionado (só o real vai para o repositório)
```

**Design:** N/A — ferramenta de linha de comando.

**Arquivos/módulos afetados:** ver `touches`.
- `tests/Vitrine.Evals/` (`Program.cs`, `Dataset/`, `Evaluators/`, `Rubric/`, `Reporting/`, `Datasets/listing-improvement/v1/cases.jsonl`, `Reports/.gitkeep`).
- `Vitrine.slnx`.
- Testes em `tests/Vitrine.UnitTests/Evals/` e `tests/Vitrine.IntegrationTests/Evals/`.
- `docs/runbooks/avaliacao-ia.md`.

### 6.1 Mapa de Comportamentos
| Cenário | Condição / Entrada | Resultado esperado | Testes |
|---|---|---|---|
| Limites | sugestão acima do limite / no limite | fail / pass | UT-01 |
| Texto puro | tag ou entidade HTML na descrição | fail | UT-02 |
| Sem contato | e-mail, telefone, URL, "whatsapp" ou emoji | fail | UT-03 |
| Valores permitidos | atributo desconhecido, `read_only`, valor ou unidade fora da lista | fail | UT-04 |
| Grounding | número, medida ou termo próprio ausente do original | fail com os termos | UT-05 |
| Título bloqueado | `titleLocked` com título sugerido | fail | UT-06 |
| Sem preencher o não derivável | atributo de `mustNotFill` preenchido | fail | UT-06 |
| Rubrica | notas estruturadas do juiz | aprovado com todas ≥ 4; limiar de 90% | UT-07 |
| Dataset válido | v1 | 30 a 50 casos, ids únicos, tipos difíceis ≥ 3 cada, ≥ 6 categorias, schema válido | UT-08 |
| Execução protegida | `anthropic` sem chave; sem `--yes`; `--rubric` com `simulated` | exit 2; exit 4 sem chamada; exit 2 | UT-09 |
| CI com simulado | dataset v1, provedor `simulated` | 100% nos determinísticos; relatório; 0 token e US$ 0 | IT-01 |
| Adaptador real no harness | respostas gravadas da Anthropic por WireMock | tokens e custo no relatório; zero chamada real | IT-02 |
| Avaliador não é vazio | gerador sabotado (inventa número, valor fora da lista, e-mail) | os avaliadores certos reprovam; exit 1 | IT-03 |
| Erros de IA | indisponível em parte dos casos | `error`, uma reexecução; > 10% dá exit 3 | IT-02 |
| Mesmo caminho do produto | `AddListingImprover` e o gerador da SPEC-0035 | executor usa a porta e a validação pós-IA | CT-01 |

## 7. Artefato B — Plano de Testes (TDD)
<!-- Todo teste tem um ID e é escrito ANTES da implementação (Red). No código, cada teste carrega a tag `SPEC-0037:<ID>` — o G1 (`spec_graph.py verify`) verifica. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem testes: "N/A — motivo" SEM ID. Teste que protege comportamento existente e passa antes da mudança: marque `(guarda: passa antes da mudança)`. Estratégia completa: references/testing.md da skill. -->

### 7.1 Testes de Caracterização
- N/A — projeto novo; não altera código existente além do `Vitrine.slnx`.

### 7.2 Testes Unitários
- **UT-01** — Dado o avaliador `LIMITS`, quando a sugestão tem título com `maxTitleLength + 1` caracteres ou descrição acima do limite (ou de 50.000 sem limite na categoria), então reprova com o motivo; no limite exato, aprova.
- **UT-02** — Dado `PLAIN_TEXT`, quando a descrição tem `<p>`, `<br/>`, `&amp;` ou `&#169;`, então reprova; texto com `\n`, acentos e "&" solto aprova.
- **UT-03** — Dado `NO_CONTACT`, quando título ou descrição têm e-mail, telefone (com e sem máscara e DDD), URL (com e sem esquema), "WhatsApp" ou emoji (inclusive com seletor de variação), então reprova, citando o trecho.
- **UT-04** — Dado `ALLOWED_VALUES`, quando um atributo sugerido não existe na categoria, é `read_only` ou `hidden`, tem valor fora de `values` ou unidade fora de `allowedUnits`, então reprova; nome de valor sem diferença de maiúsculas e acentos aprova.
- **UT-05** — Dado `GROUNDING`, quando a sugestão traz "Bluetooth 5.3", "500 mL" ou "Samsung" e o original não, então reprova listando os termos; com os termos no original (inclusive "500ml" × "500 mL"), aprova.
- **UT-06** — Dado `TITLE_LOCK` e `NO_INVENTED_FILL`, quando há título sugerido num caso `titleLocked` ou um atributo de `mustNotFill` preenchido, então reprovam; sem eles, aprovam.
- **UT-07** — Dado respostas estruturadas do juiz (gravadas), quando a rubrica é calculada, então um caso com todas as notas ≥ 4 é aprovado, com alguma nota 3 é reprovado, e 36 de 40 aprovados (90%) atingem o limiar, 35 não.
- **UT-08** — Dado o dataset v1, quando validado, então tem entre 30 e 50 casos, `caseId` único, todas as linhas válidas no schema do §6, ao menos 6 categorias e ao menos 3 casos com cada tag difícil (`sold`, `catalog`, `contact_in_description`, `underivable_required`, `no_description`, `numbers`).
- **UT-09** — Dado a linha de comando, então:
  - `--provider anthropic` sem chave sai com exit 2, sem chamada HTTP;
  - com chave e sem `--yes`, imprime a estimativa de custo e sai com exit 4, sem chamada;
  - `--rubric` com `simulated` sai com exit 2;
  - a chave nunca aparece na saída do console.

### 7.3 Testes de Integração
- **IT-01** — Dado o dataset v1 e o provedor `simulated`, quando a ferramenta roda em processo (como no CI), então todos os avaliadores determinísticos ficam em 100%, o relatório JSON e Markdown é gerado num diretório temporário com os campos do §6, os tokens são 0, o custo é US$ 0, o exit é 0 e o tempo é < 30 s.
- **IT-02** — Dado `--provider anthropic --yes`, chave sintética e o WireMock no lugar da API da Anthropic, servindo as respostas gravadas da SPEC-0035 (`tests/Fixtures/Anthropic/`) para um subconjunto de 5 casos (`--limit 5`), quando a ferramenta roda, então o relatório soma tokens e custo pelo `usage`, nenhuma requisição sai para outro host, e a chave não aparece no relatório. Com 529 em 1 dos 5 casos, o caso é reexecutado uma vez; persistindo, fica `error`, e com 2 de 5 (> 10%) o exit é 3.
- **IT-03** — Dado um `IListingImprover` sabotado que inventa "Bluetooth 5.3" no título, sugere `COLOR` "Roxo" (fora da lista) e põe um e-mail na descrição, quando a ferramenta roda sobre o v1 sem a validação pós-IA, então `GROUNDING`, `ALLOWED_VALUES` e `NO_CONTACT` reprovam os casos afetados, a taxa crua aparece no relatório, e o exit é 1. Com a validação pós-IA, `ALLOWED_VALUES` e `NO_CONTACT` passam, e `GROUNDING` continua reprovando (o produto só avisa), o que prova que o limiar de grounding mede o modelo.

### 7.4 Testes de Contrato
- **CT-01** — Dado o contrato SPEC-0035@1, quando o executor da eval monta a entrada a partir de um caso do dataset e chama `ListingImprovementGenerator` com o adaptador registrado por `AddListingImprover` (configuração `Ai:ListingImprover:Provider=simulated`), então usa os tipos públicos `ImprovementInput`/`ImprovementDraft`, recebe a sugestão validada e o `AiUsage`, sem duplicar a montagem do produto. É o lado consumidor do contrato.

### 7.5 Testes E2E
- N/A — ferramenta de linha de comando, sem jornada de usuário.

### 7.6 Outros
- Avaliação real (manual, go-live, SPEC-0009, com o usuário): `dotnet run --project tests/Vitrine.Evals -- --provider anthropic --rubric --yes` com a chave do usuário. Critério: 100% nos determinísticos e ≥ 90% na rubrica, com o relatório versionado e anexado ao PR que liga o provedor (ADR-0015). Não é teste do CI.

**Dublês e dados de teste:**
- dataset sintético v1;
- WireMock com as respostas gravadas da SPEC-0035;
- adaptador sabotado (IT-03);
- respostas gravadas do juiz da rubrica.

**Ambiente de execução:** UT e IT em processo (local e CI), sem rede externa e sem chave. A avaliação real roda na máquina do usuário ou do Architect, no go-live.

## 8. Plano de Rollout
- **Estratégia:** deploy direto. É ferramenta de teste: não entra na imagem da Api.
- **Dados/schema:** N/A.
- **Compatibilidade:** `Vitrine.slnx` ganha o projeto. O `dotnet test --solution` não executa a ferramenta (não é projeto de teste); os testes dela rodam pelos projetos de teste existentes.
- **Observabilidade:** relatório versionado de cada execução real; custo real somado pelo `usage`.
- **Rollback:** revert do merge.
- **Etapas de migração/coexistência:** N/A.

## 9. Questões em Aberto
- [x] Dataset real ou sintético? — Sintético no v1 (40 casos), porque ainda não há anúncios reais conectados. O v2, com anúncios anonimizados do usuário, entra no go-live. (Architect, 2026-11-02, a confirmar no H1)
- [x] Os avaliadores reaproveitam as regras do produto? — Não. São implementados à parte, como oráculo independente; o IT-03 prova que não são vazios. (Architect, 2026-11-02, a confirmar no H1)
- [x] Qual modelo julga a rubrica? — `claude-sonnet-5-5` (ADR-0015), configurável. A rubrica é opcional e também custa dinheiro. (Architect, 2026-11-02, a confirmar no H1)
- [x] O grounding com 100% não é rígido demais para o modelo real? — É a regra inegociável da ADR-0015. Se a avaliação real não atingir 100%, o prompt e o effort são ajustados antes de ligar o provedor; o produto ainda avisa e desmarca o campo sem origem (SPEC-0035/0038). (Architect, 2026-11-02, a confirmar no H1)

## 10. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado". O arquiteto nunca aprova a própria spec.

## 11. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. Cada fase começa pelos testes. -->
**Fase 1: Avaliadores determinísticos**
- [ ] Red: escrever UT-01..UT-09 com a tag `SPEC-0037:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: limites, texto puro, valores permitidos, grounding e agregação de resultados
- [ ] Refactor mantendo tudo verde

**Fase 2: Suíte e execução**
- [ ] Red: escrever IT-01..IT-03 e CT-01 com a tag `SPEC-0037:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: `tests/Vitrine.Evals` com 40 casos sintéticos, execução com o gerador simulado no CI e juiz opcional (só manual, no go-live)
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
<!-- Mudança em spec aprovada: uma linha por emenda. Mudou o contrato? Incremente `contract_version` e rode `spec_graph.py impacted SPEC-0037`. -->
| Versão do contrato | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|

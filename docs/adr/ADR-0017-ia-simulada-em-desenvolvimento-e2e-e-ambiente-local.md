---
id: ADR-0017
title: IA simulada em desenvolvimento, E2E e ambiente local
status: accepted
origin: decision
date: 2026-11-02
pillars: [testes, custo]
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de arquitetura (só Vitrine.Listings.Infrastructure referencia Anthropic — SPEC-0035:UT-07) + validação das opções no host (SPEC-0035:UT-06) + teste de ferramenta do AppHost e do Compose (SPEC-0035:IT-09)
---

# ADR-0017 — IA simulada em desenvolvimento, E2E e ambiente local

## Contexto e Problema
A melhoria de anúncios (épico SPEC-0032) chama a IA a cada sugestão. A ADR-0015 escolheu o Claude Opus 5.5 pelo SDK oficial, atrás da porta `IListingImprover`, e a ADR-0012 mostra que a IA é o maior custo variável do produto (≈ US$ 0,06 por melhoria). O usuário pediu que nada gere custo antes do go-live (SPEC-0009).

Ao mesmo tempo, a jornada `aprovar-melhoria` precisa rodar inteira no `aspire run`, no E2E do CI e no ambiente local prod-like, como a `conectar-conta` roda com o simulador do ML (ADR-0016). A ADR-0002 proíbe chamar serviços externos reais no CI, e o E2E precisa de resultados determinísticos.

Como ter a jornada completa, com a IA no meio, sem custo e sem chave, sem deixar o caminho real sem teste?

## Direcionadores da Decisão
- "Sem custo até o go-live": nenhuma chamada paga em dev, CI, E2E ou ambiente local (instrução do usuário).
- E2E determinístico, offline e sem segredo no CI (ADR-0002).
- O adaptador real precisa estar pronto e testado no go-live, sem reescrever nada (ADR-0015: trocar o modelo por configuração).
- Impossível ligar o provedor real por engano, e impossível servir sugestão falsa a vendedor real em produção.
- A regra "não inventar fatos" vale também para a sugestão simulada: o E2E não pode depender de texto que o modelo real nunca produziria.

## Opções Consideradas
- **A. Gerador simulado determinístico in-process**, atrás da porta `IListingImprover`, escolhido por configuração (`Ai:ListingImprover:Provider = simulated | anthropic`, padrão `simulated`). O adaptador real é testado por contrato com respostas gravadas da Messages API servidas por WireMock.
- **B. WireMock imitando a API da Anthropic** em dev e E2E, com o adaptador real sempre ligado: respostas fixas por mapeamento, sem relação com o anúncio pedido, e um serviço a mais no AppHost e no Compose.
- **C. Claude real em todos os ambientes:** fiel, mas com custo a cada execução do E2E, chave no CI e respostas não determinísticas.

## Resultado da Decisão
**Opção escolhida:** "A. Gerador simulado determinístico in-process", porque:
- não custa nada, não precisa de chave nem de rede, e responde sempre igual para a mesma entrada (E2E estável);
- gera a sugestão a partir do próprio anúncio (título reorganizado dentro do limite, descrição em texto puro só com os dados do anúncio, atributo só quando o valor aparece no anúncio e está na lista permitida), o que exercita a validação pós-IA, o grounding e a tela com dados coerentes;
- o adaptador real continua no código e no CI, testado por contrato com respostas gravadas (sucesso, recusa, `max_tokens`, 529), sem chamada paga, e é ligado no go-live só por configuração.

**Regras:**
- O padrão é `simulated`. `anthropic` exige `Ai:Anthropic:ApiKey` (segredo, ADR-0006), e o host recusa subir sem ela.
- Em `Production`, `simulated` só sobe com `Ai:ListingImprover:AllowSimulated=true`, que só o `deploy/local/.env.example` define (mesmo padrão do `MercadoLivre:AllowNonOfficialEndpoints`, ADR-0016). Assim, staging e produção nunca servem sugestão simulada a vendedor real.
- O AppHost (dev e E2E) define `simulated` de forma explícita, e nenhum arquivo versionado tem chave.
- A suíte de avaliação (SPEC-0037) roda no CI com `simulated`. A execução real é manual, exige chave e confirmação de custo, e é pré-requisito para ligar o provedor real no go-live, junto com o usuário.
- A sugestão simulada é marcada (`provider: "simulated"`) na API e no registro de custo (custo zero).

### Consequências
- **Boa**, porque a jornada inteira roda local e no CI sem custo e sem conta externa, e o usuário testa tudo no ambiente local antes do go-live.
- **Boa**, porque ligar o provedor real é uma mudança de configuração, já coberta por testes de contrato.
- **Ruim**, porque a qualidade do texto simulado não diz nada sobre a qualidade do modelo real. Mitigação: a avaliação real (SPEC-0037) é obrigatória antes de ligar.
- **Ruim**, porque as respostas gravadas podem divergir da API real com o tempo. Mitigação: elas seguem o formato documentado da Messages API (com a data), e a avaliação real no go-live exercita o adaptador de verdade.
- **Ruim**, porque é mais um adaptador para manter. O escopo é pequeno e determinístico, testado em unidade.

### Confirmação (G3)
- Teste de arquitetura: só `Vitrine.Listings.Infrastructure` referencia o assembly `Anthropic` (SPEC-0035:UT-07).
- Validação das opções no host: `Provider=anthropic` sem `Ai:Anthropic:ApiKey` reprova a subida; `simulated` em `Production` sem `AllowSimulated` reprova a subida (SPEC-0035:UT-06).
- Teste de ferramenta: o AppHost passa `simulated` à Api; o `deploy/compose.yaml` tem `simulated` como padrão; só o `deploy/local/.env.example` liga `AllowSimulated`; nenhum arquivo versionado define a chave (SPEC-0035:IT-09).
- O CI e o E2E sempre usam `simulated`: o E2E sobe pelo AppHost (regra acima), e a suíte de avaliação no CI roda com `simulated` (SPEC-0037:IT-01).

## Prós e Contras das Opções
| Critério (peso) | A. Simulado in-process | B. WireMock da Anthropic | C. Claude real |
|---|---|---|---|
| Custo zero até o go-live (3) | sim | sim | não |
| Determinístico e offline no CI (3) | sim | sim | não |
| Sugestão coerente com o anúncio pedido (2) | sim (gerada do anúncio) | não (resposta fixa) | sim |
| Adaptador real testado antes do go-live (2) | sim (contrato com respostas gravadas) | sim | sim |
| Peças a mais no AppHost/Compose (1) | nenhuma | um serviço | nenhuma |
| Fidelidade à qualidade do modelo (1) | baixa (coberta pela eval real) | baixa | alta |

## Mais Informações
- ADR-0015 (provedor de IA, porta `IListingImprover`, SDK `Anthropic`, suíte de avaliação), ADR-0012 (custo por workspace, ≈ US$ 0,06 por melhoria, limite de gasto na Anthropic), ADR-0002 (eval manual; nada externo no CI), ADR-0016 (mesmo padrão para o ML), ADR-0006 (chave como segredo).
- Modelo e preço (não verificado na página oficial em 2026-11-02; valores da referência da Anthropic em cache de 2026-10-06, a conferir em https://platform.claude.com/docs/en/about-claude/pricing no go-live): `claude-opus-5-5`, US$ 4/US$ 20 por milhão de tokens de entrada/saída e US$ 0,20 de cache lido. Saída estruturada por `output_config.format`; `stop_reason: "refusal"` tratado como recusa.
- Relacionadas: SPEC-0032 (épico), SPEC-0035 (porta, adaptadores e configuração), SPEC-0037 (suíte de avaliação), SPEC-0038 (E2E com a IA simulada), SPEC-0009 (go-live: ligar o provedor real com o usuário).

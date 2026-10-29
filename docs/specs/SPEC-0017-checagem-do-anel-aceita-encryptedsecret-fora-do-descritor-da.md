---
id: SPEC-0017
title: Checagem do anel aceita encryptedSecret fora do descritor da chave
tier: lite
type: fix
user_facing: false
status: in-progress
created: 2026-10-29
parent: SPEC-0001
depends_on: [SPEC-0014]
consumes_contract: []
touches: [src/Api/Security/DataProtectionKeyRingCheck.cs, tests/Vitrine.IntegrationTests/Security/**]
adrs: [ADR-0006]
external: []
size: S
approved_by: thomas
approved_at: 2026-10-29
approved_via: piloto automático autorizado no chat em 2026-10-29
---

# SPEC-0017 — Checagem do anel aceita encryptedSecret fora do descritor da chave

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
Reproduzido pelo Reviewer no G4 da SPEC-0014: um `<key>` cujo descritor guarda a chave em claro, mas que contém em um filho qualquer (ex.: `<junk>`) um `<encryptedSecret><masterKeyEncrypted/>` válido copiado de outra chave, passa pela checagem do anel fora de Development, e o Data Protection carrega a chave em claro. Esperado: a subida é recusada, como para qualquer chave sem a cifra da chave mestra. Impacto: exige escrita no banco, mas anula o controle da SPEC-0008 (chaves do anel sempre cifradas pela chave mestra).

## 2. Causa Raiz
`src/Api/Security/DataProtectionKeyRingCheck.cs`: `key.Descendants(EncryptedSecret)` aceita o elemento em qualquer profundidade do `<key>`, em vez de exigir a posição em que o Data Protection o lê (`key/descriptor/descriptor/encryptedSecret`). O filtro de `<key>` usa `Name.LocalName`, enquanto o Data Protection só reconhece `key` sem namespace.

## 3. Mudança Proposta
A checagem exige o `encryptedSecret` (com `masterKeyEncrypted`) exatamente na posição do descritor, com `decryptorType` que resolva para o decifrador da chave mestra do Vitrine (`MasterKeyXmlDecryptor`, comparado pelo nome do tipo como o framework faz, sem depender da versão do assembly) e recusa qualquer `<key>` cujo descritor tenha segredo em claro; o filtro de chave passa a ser o `XName` `key` sem namespace, igual ao Data Protection. Não muda: mensagens de erro, Development, revogações ignoradas, rotação.

**Padrão seguido:** `DataProtectionKeyRingCheck.cs` e `MasterKeyXmlEncryption.cs` (SPEC-0008/0014); testes em `KeyRingRevocationTests.cs` e `MasterKeyTests.cs`.

**Rollback:** revert do commit.

**Outras ocorrências:** Nenhuma. (A contagem de revogações como chaves atuais no log de rotação de `DataProtectionMigrations.cs` é só texto de log; fica registrada aqui, sem spec.)

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0017:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — área já coberta pelos testes de chave mestra e de revogação das SPEC-0008/0014.
- Unitários: N/A — o defeito aparece na subida do host com o anel real no PostgreSQL.
- - **IT-01** — Dado um anel no PostgreSQL com uma `<key>` de descritor em claro contendo, fora do descritor, um `encryptedSecret` válido copiado de outra chave, quando o host sobe fora de Development, então a subida falha com a mensagem de chave sem a cifra da chave mestra; um `<key>` com `encryptedSecret` de outro `decryptorType` (ex.: `NullXmlDecryptor` com o segredo em claro dentro de `masterKeyEncrypted`, isolado, como cópia de um segredo legítimo com o tipo trocado, ou ao lado de um `<masterKey>` em claro sem o atributo `requiresEncryption`) também impede a subida; um `<key>` em claro em outro namespace é ignorado como pelo Data Protection (o host sobe e a chave não é carregada); e um anel válido (com e sem revogações) continua subindo.

## 5. Questões em Aberto
<!-- Dúvidas que impedem fechar a spec, respondidas ANTES do H1. Aberta: `- [ ] pergunta (quem responde)`. Respondida: `- [x] pergunta — resposta (quem, data)`. Com alguma aberta o G0 reprova. Sem dúvidas: escreva "Nenhuma". Dúvida que surge depois da aprovação vira impedimento (seção Registro de Impedimentos). -->
Nenhuma.

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever IT-01 com a tag `SPEC-0017:<ID>` e confirmar que falham pelo motivo certo
- [ ] Green: checagem do anel exige `encryptedSecret` na posição do descritor e filtra `key` pelo `XName` sem namespace
- [ ] Validar: build + suíte completa + arquitetura (G2/G3)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — d36c0ff (árvore suja) | 2026-10-29 |
| G1 Red | PASS | verify G1: PASS; `dotnet test tests/Vitrine.UnitTests && npm --prefix web run test` exit 127 (red: sh: 1: vitest: not found) — d47fe37 | 2026-10-29 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (Duration  449ms (environment 69%, transform 13%, import 12%, tests 4%, worker 2%)); lint exit 0 (> tsc --noEmit && tsc --noEmit -p tsconfig.node.json); coverage exit 0 (Coverage: 100%) — 05d9f71 | 2026-10-29 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 1s 042ms) — 05d9f71 | 2026-10-29 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: APPROVED (Reviewer independente, 2ª rodada + delta) em 05d9f71: 39 sondas sem a chave forjada carregada com segredo em claro (NullXmlDecryptor e variantes, decryptorType com caixa/espaço/culture/sufixo/aninhado/ausente/DPAPI/EncryptedXml/namespace antigo, descritor duplicado, comentários/CDATA); descritores reais GCM/CBC/Managed, revogações e rotação sobem; mutações removendo cada defesa detectadas pelo IT-01; catch geral como o DefaultTypeNameResolver; IT-01 10/10; nit: decryptorType não carregável aceito pela checagem, chave inutilizada sem vazamento — 05d9f71 | 2026-10-29 |
| G5 Integração & CI | PENDING | | |
| H2 Integração aprovada | PENDING | | |
| G6 Deploy | PENDING | | |
| G7 Pronto & Docs | PENDING | | |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|
| IMP-01 | 2026-10-29 | G2 | trabalho | TEST_DEFECT: em KeyRingDescriptorTests.cs:64-67 a mensagem do Assert.True chama LoadsKey(again, ...) sempre, que sobe o host de novo e relança a exceção esperada; o caso principal não passa com nenhuma implementação correta | Implementer: correção pronta (não commitada); 4/5 do IT-01 e 53/54 do namespace Security passam | Test-writer | SPEC-0017 — Test-writer 5938fcf: LoadsKey só roda quando o host sobe indevidamente; Security 54/54 com a correção, caso principal falha sem ela | 2026-10-29 |

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue

### Como foi feito

### Prova de Correção
<!-- type fix: teste de regressão falhou antes (commit red + saída) e passa depois (commit green + execução). Outros tipos: "N/A". -->

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|

### Definição de Pronto
- [ ] Teste de regressão falhou antes e passa depois da correção
- [ ] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [ ] Review independente sem achados blocker/major (G4)
- [ ] Padrão existente mantido
- [ ] Disponível no ambiente-alvo via pipeline (G6)
- [ ] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [ ] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy

### Pendências

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.2 (escopo) | 2026-10-29 | A checagem exige `decryptorType` do decifrador da chave mestra em cada `encryptedSecret` do descritor; IT-01 ganha os casos com `NullXmlDecryptor`. Mantida a semântica atual da chave errada (basta uma chave do anel decifrar): com o decifrador travado, chave que não decifra não é carregada | G4 CHANGES_REQUESTED: `NullXmlDecryptor` no `decryptorType` burlava a checagem e o Data Protection carregava o segredo em claro (reproduzido) | nenhuma | thomas (2026-10-29, piloto automático) |
| v1.1 (esclarecimento) | 2026-10-29 | IT-01 explicita o caso do `<key>` em outro namespace, consequência do filtro pelo `XName` `key` sem namespace já previsto na Mudança Proposta | Test-writer: o caso derivava da seção 3 mas não estava no texto do teste | nenhuma | thomas (2026-10-29, piloto automático) |

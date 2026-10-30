---
id: ADR-0016
title: Simulador do Mercado Livre para desenvolvimento, E2E e ambiente local
status: accepted
origin: decision
date: 2026-10-30
pillars: [testes]
decision_makers: [thomas]
consulted: []
informed: []
supersedes:
superseded_by:
enforced_by: teste de ferramenta (SPEC-0024:IT-03) que reprova o simulador fora de Development/E2E/local + revisão no G4
---

# ADR-0016 — Simulador do Mercado Livre para desenvolvimento, E2E e ambiente local

## Contexto e Problema
A jornada `conectar-conta` passa pelo Mercado Livre em três pontos:
- a página de autorização OAuth no navegador;
- a troca do code pelo token;
- a leitura de `/users/me` e dos anúncios.

O Mercado Livre não tem sandbox: os usuários de teste são contas reais, limitadas a 10 por titular e removidas após 60 dias sem uso. O redirect URI precisa ser HTTPS e bater exatamente com o cadastrado. No Brasil só se permite 1 aplicação por titular, depois da validação dos dados.

Os testes de integração já usam WireMock para HTTP externo (SPEC-0003:IT-03). O E2E no navegador e o teste manual local, porém, precisam de um ML que **redirecione o navegador**, valide PKCE e gire os refresh tokens, sem credencial real e sem internet. Como rodar a jornada inteira localmente e no CI?

## Direcionadores da Decisão
- "Tudo rodando localmente e testado pelo usuário antes de gerar custo ou exigir conta externa" (instrução do usuário).
- O E2E é determinístico no CI e não depende de rede externa nem de segredo (ADR-0002).
- O comportamento fiel ao contrato documentado do ML inclui:
  - PKCE S256;
  - `state` devolvido sem validação;
  - refresh token de uso único e rotacionado;
  - `invalid_grant`;
  - `/items/bulk`;
  - `search_type=scan`.
- Nenhum caminho do simulador pode chegar à produção.

## Opções Consideradas
- **A. Projeto ASP.NET mínimo `tools/MercadoLivreSimulator`**, com estado em memória:
  - roda como recurso do AppHost (dev/E2E) e como serviço de perfil do Compose local;
  - tem endpoints de controle `/__sim/*` para os testes.
- **B. WireMock.Net standalone com mapeamentos JSON:** já é dependência dos testes, mas a lógica de estado (PKCE, rotação, scan) exige extensões em C# de qualquer jeito, e a página de autorização vira HTML estático sem fluxo real.
- **C. Conta e usuários de teste reais do ML no E2E:** é fiel, mas depende de rede, de segredo no CI e de contas que expiram. Também não roda sem o app no DevCenter.

## Resultado da Decisão
**Opção escolhida:** "A. Projeto ASP.NET mínimo", porque:
- cobre o fluxo de navegador e o estado do OAuth com o mesmo stack do time (.NET 10, Minimal API), sem biblioteca nova;
- roda offline no CI;
- pode ser ligado no ambiente local prod-like para o teste manual do usuário.

Os testes de integração continuam com WireMock (padrão da SPEC-0003), simulando cada resposta e cada erro do ML pontualmente.

### Consequências
- **Boa:** a jornada completa, incluindo reconexão, expiração e `invalid_grant`, roda local e no CI sem conta externa.
- **Boa:** o usuário testa tudo em `https://vitrine.localhost` antes de criar o app no DevCenter.
- **Ruim:** o simulador pode divergir do ML real. Mitigações:
  - ele implementa só o contrato documentado e citado na SPEC-0024, com fontes e datas;
  - o teste manual com um usuário de teste real do ML, depois de criado o app no DevCenter, é pendência registrada na SPEC-0019, antes do go-live (SPEC-0009).
- **Ruim:** é mais um projeto para manter. O escopo fica restrito aos endpoints que o produto usa.

### Confirmação (G3)
- A imagem da Api não referencia o projeto do simulador (teste de solução/arquitetura).
- O serviço do simulador no Compose fica só no perfil `ml-simulator`, ativado apenas em `deploy/local/.env.example` (teste de ferramenta SPEC-0024:IT-03).
- A Api só aceita URLs do ML diferentes das oficiais fora de `Production` ou com `MercadoLivre:AllowNonOfficialEndpoints=true`, valor que o ambiente local define e staging/produção não (SPEC-0020:UT-04).

## Prós e Contras das Opções
| Critério (peso) | A. Projeto ASP.NET | B. WireMock standalone | C. ML real |
|---|---|---|---|
| Fluxo de navegador e estado OAuth (3) | completo | parcial (extensões C#) | completo |
| Offline e determinístico no CI (3) | sim | sim | não |
| Sem segredo/conta externa (2) | sim | sim | não |
| Fidelidade ao ML real (2) | média (contrato documentado) | média | alta |
| Manutenção (1) | média | média | baixa |

## Mais Informações
Documentação do Mercado Livre consultada em 30/10/2026:
- Autenticação e autorização (PKCE, `state`, tokens de 6 h, refresh token de 6 meses, uso único e rotacionado, `invalid_grant`): https://developers.mercadolivre.com.br/pt_br/autenticacao-e-autorizacao (atualizada em 29/12/2025).
- Criação da aplicação (redirect HTTPS, 1 app por titular no Brasil, PKCE recomendado): https://developers.mercadolivre.com.br/pt_br/crie-uma-aplicacao-no-mercado-livre.
- Usuários de teste (10 por conta, removidos após 60 dias, sem sandbox): https://developers.mercadolivre.com.br/pt_br/realizacao-de-testes.
- Itens e buscas (`search_type=scan`, `/items/bulk` substituindo `/items?ids=` até 25/10/2026): https://developers.mercadolivre.com.br/pt_br/itens-e-buscas (atualizada em 31/08/2026).

Relacionadas: SPEC-0019 (épico) e SPEC-0024 (implementação).

---
id: SPEC-0039
title: Ambiente local prod-like escuta na internet
tier: lite
type: fix
user_facing: false
status: approved
created: 2026-11-02
parent: SPEC-0032
depends_on: []
consumes_contract: []
touches: [deploy/compose.override.yaml, deploy/local/.env.example, tests/Vitrine.Tooling.Tests/CaddyBindAddressTests.cs, docs/runbooks/deploy.md]
adrs: [ADR-0004, ADR-0006]
external: []
size: S
approved_by: thomas
approved_at: 2026-11-02
---

# SPEC-0039 — Ambiente local prod-like escuta na internet

<!-- Tier lite: bug fix ou mudança pequena, um módulo, sem mudança de contrato público, tamanho S. type: fix | feature | refactor. user_facing: true se o defeito aparece numa jornada do usuário (o teste de regressão deveria ser E2E). Se qualquer condição falhar, use o tier full. Substitua todos os marcadores com chaves duplas. -->

## 1. Problema
O ambiente local prod-like (SPEC-0005, com o simulador da SPEC-0024) publica o Caddy em `0.0.0.0:80` e `0.0.0.0:443` (e `443/udp`). Numa máquina com IP público, como a do Architect, o Vitrine local fica acessível pela internet. O ufw não protege, porque o Docker publica portas por regras próprias do iptables, que passam antes das do ufw.

- **Reprodução (Architect, 2026-11-02):** `deploy/deploy.sh <sha> --env-file deploy/local/.env`; de outra máquina, `curl -k -H 'Host: vitrine.localhost' https://<IP público>/health/live` responde 200.
- **Impacto:** o ambiente local tem cadastro, conta de teste, simulador do ML com credenciais sintéticas e, a partir deste épico, a IA simulada liberada (`AI_ALLOW_SIMULATED`). Tudo isso fica exposto a terceiros.
- **Esperado:** no ambiente local, as portas escutam só em `127.0.0.1`. Em staging e produção (SPEC-0009), continuam em todas as interfaces.

## 2. Causa Raiz
`deploy/compose.override.yaml`, serviço `caddy`:
```yaml
    ports:
      - "80:80"
      - "443:443"
      - "443:443/udp"
```
Sem endereço de host, o Docker publica em `0.0.0.0` (e `::`). O mesmo arquivo serve o ambiente local e o servidor. Não existe variável para restringir a interface, e o `deploy/local/.env.example` não tem como pedir isso.

## 3. Mudança Proposta
- `deploy/compose.override.yaml`: as três portas passam a `"${CADDY_BIND_ADDRESS:-0.0.0.0}:80:80"`, `"${CADDY_BIND_ADDRESS:-0.0.0.0}:443:443"` e `"${CADDY_BIND_ADDRESS:-0.0.0.0}:443:443/udp"`. O padrão `0.0.0.0` mantém staging e produção como estão.
- `deploy/local/.env.example`: `CADDY_BIND_ADDRESS=127.0.0.1`, com comentário explicando por que (Docker passa por cima do ufw).
- `docs/runbooks/deploy.md`:
  - a variável na tabela de variáveis do `.env`;
  - nota para quem já tem `deploy/local/.env` (copiar a linha);
  - acesso de outra máquina por túnel SSH (`sudo ssh -N -L 443:127.0.0.1:443 <servidor>` e `vitrine.localhost` resolvendo para `127.0.0.1` no cliente; a porta 443 local mantém os redirects do OAuth do simulador válidos);
  - verificação: `ss -ltn` mostra `127.0.0.1:443`, e o IP público não responde.
- **Não muda:** o `Caddyfile`, os sites, a rede `proxy`, os perfis do Compose e o comportamento em staging e produção. O smoke continua usando `--resolve vitrine.localhost:443:127.0.0.1`.

**Padrão seguido:**
- variáveis com padrão no Compose, como `ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Production}` e `CADDY_EXTRA_SITES_DIR` (SPEC-0024);
- valor só no ambiente local por `deploy/local/.env.example`;
- teste de ferramenta com `docker compose config` em `tests/Vitrine.Tooling.Tests/MercadoLivreSimulatorDeployTests.cs`.

**Rollback:** revert do commit.

**Outras ocorrências:** nenhuma. Conferido em `deploy/compose.yaml` e `deploy/compose.override.yaml` (inclusive os perfis `db-container`, `observability` e `ml-simulator`): o Caddy é o único serviço com `ports`; os demais só usam redes internas.

## 4. Plano de Testes (TDD)
<!-- Mínimo: um teste de regressão que reproduz o problema e FALHA antes da correção, no nível em que o defeito aparece (IT para fronteiras de I/O, E2E para jornadas de usuário). Código sem cobertura: primeiro CH-xx fixando o comportamento atual. Tag no código: `SPEC-0039:<ID>`. Modo privado: sem tag no código; aponte o teste no fim da linha com `→ caminho::nome` (ex.: `→ tests/test_soma.py::test_soma`). Categoria sem teste: "N/A — motivo" SEM ID. -->
- Caracterização: N/A — os testes de deploy das SPEC-0005 e SPEC-0024 (`tests/infra`, `MercadoLivreSimulatorDeployTests`) seguem como guarda.
- Unidade: N/A — o defeito está na configuração do Compose, verificada pelo `docker compose config` no IT-01.
- **IT-01** — Dado o Compose lido por `docker compose config` (mesmos arquivos do `deploy/deploy.sh`), quando é resolvido com o `deploy/local/.env.example`, então as três portas publicadas do `caddy` têm `host_ip: 127.0.0.1`. Quando é resolvido sem `CADDY_BIND_ADDRESS` (configuração de staging/produção), então as portas continuam em `0.0.0.0`. Falha antes da correção, porque hoje o `host_ip` não existe e o Docker usa todas as interfaces.

## 5. Questões em Aberto
- [x] Restringir também staging e produção? — Não. Lá o Caddy é a porta de entrada pública, e o firewall é o da nuvem (SPEC-0005/0009). (Architect, 2026-11-02, a confirmar no H1)
- [x] E o IPv6 (`::1`)? — `127.0.0.1` cobre o uso local: os navegadores e o `curl --resolve` do smoke usam IPv4 em `*.localhost`, com recuo para IPv4 quando `::1` não responde. Se precisar de IPv6, o `.env` pode trocar o valor. (Architect, 2026-11-02, a confirmar no H1)

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [ ] Red: escrever IT-01 com a tag `SPEC-0039:<ID>` e confirmar que falha pelo motivo certo
- [ ] Green: `CADDY_BIND_ADDRESS` no Compose (padrão `0.0.0.0`; `127.0.0.1` no `deploy/local/.env.example`) e runbook do túnel SSH
- [ ] Validar: build + suíte completa (G2/G3)

**Fase final: Integração, entrega e documentação**
- [ ] Review independente (G4)
- [ ] Integração + CI verde (G5) e aprovação (H2)
- [ ] Deploy no ambiente local prod-like com smoke (G6)
- [ ] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — b44124e (árvore suja) | 2026-11-02 |
| G1 Red | PASS | MANUAL: red b521a31: dotnet test --project tests/Vitrine.Tooling.Tests -- --filter-trait 'spec=SPEC-0039:IT-01' → total 2, failed 1 (Ambiente_local_publica_o_Caddy_so_em_127_0_0_1: 80/tcp, 443/tcp, 443/udp sem host_ip; esperado 127.0.0.1); guarda de staging/produção verde. Teste de ferramenta (Tooling), fora do red unitário do --run. | 2026-11-02 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (at least ~677ms faster with isolate: false — reuses workers across files instead of one pe); lint exit 0 ([0m[34mChecked 67 files in 36[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (No lines with coverage information in this diff.) — 8f6ffe1 | 2026-11-02 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 2s 240ms) — 8f6ffe1 | 2026-11-02 |
| G4 Review | PENDING | | |
| G5 Integração & CI | PENDING | | |
| H2 Integração aprovada | PENDING | | |
| G6 Deploy | PENDING | | |
| G7 Pronto & Docs | PENDING | | |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

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

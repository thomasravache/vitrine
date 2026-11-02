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
- `deploy/compose.override.yaml`: as três portas passam a `"${CADDY_BIND_ADDRESS:+${CADDY_BIND_ADDRESS}:}80:80"`, `"${CADDY_BIND_ADDRESS:+${CADDY_BIND_ADDRESS}:}443:443"` e `"${CADDY_BIND_ADDRESS:+${CADDY_BIND_ADDRESS}:}443:443/udp"`. Sem a variável, a porta sai sem endereço de host, exatamente como hoje: o Docker publica em `0.0.0.0` **e** `[::]`, o que mantém o IPv6 (registro `AAAA`) de staging e produção. Um `0.0.0.0` explícito publicaria só em IPv4 (Emenda v1.1).
- `deploy/local/.env.example`: `CADDY_BIND_ADDRESS=127.0.0.1`, com comentário explicando por que (Docker passa por cima do ufw).
- `docs/runbooks/deploy.md`:
  - a variável na tabela de variáveis do `.env`;
  - nota para quem já tem `deploy/local/.env` (copiar a linha);
  - acesso de outra máquina por túnel SSH (`sudo ssh -N -L 443:127.0.0.1:443 <servidor>` e `vitrine.localhost` resolvendo para `127.0.0.1` no cliente; a porta 443 local mantém os redirects do OAuth do simulador válidos);
  - verificação: `ss -ltun | grep -E ':(80|443)\b'` mostra só `127.0.0.1` (TCP e UDP), e o IP público não responde.
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
- **IT-01** — Dado o Compose lido por `docker compose config` (mesmos arquivos do `deploy/deploy.sh`), quando é resolvido com o `deploy/local/.env.example`, então as três portas publicadas do `caddy` têm `host_ip: 127.0.0.1`. Quando é resolvido sem `CADDY_BIND_ADDRESS` (configuração de staging/produção), então as portas continuam **sem** `host_ip` (todas as interfaces, IPv4 e IPv6), como hoje; `host_ip: 0.0.0.0` reprova, porque derruba o IPv6. Falha antes da correção, porque hoje o `host_ip` não existe e o Docker usa todas as interfaces.

## 5. Questões em Aberto
- [x] Restringir também staging e produção? — Não. Lá o Caddy é a porta de entrada pública, e o firewall é o da nuvem (SPEC-0005/0009). (Architect, 2026-11-02, a confirmar no H1)
- [x] E o IPv6 (`::1`)? — `127.0.0.1` cobre o uso local: os navegadores e o `curl --resolve` do smoke usam IPv4 em `*.localhost`, com recuo para IPv4 quando `::1` não responde. Se precisar de IPv6, o `.env` pode trocar o valor. (Architect, 2026-11-02, a confirmar no H1)

## 6. Aprovação (H1)
Registrada no frontmatter (`approved_by`, `approved_at`) somente depois que o humano responder "Aprovado".

## 7. Checklist de Implementação
<!-- Preenchido na fase PLAN, após a aprovação. -->
**Fase 1: Regressão e correção**
- [x] Red: escrever IT-01 com a tag `SPEC-0039:<ID>` e confirmar que falha pelo motivo certo
- [x] Green: `CADDY_BIND_ADDRESS` no Compose (padrão `0.0.0.0`; `127.0.0.1` no `deploy/local/.env.example`) e runbook do túnel SSH
- [x] Validar: build + suíte completa (G2/G3)

**Fase final: Integração, entrega e documentação**
- [x] Review independente (G4)
- [x] Integração + CI verde (G5) e aprovação (H2)
- [x] Deploy no ambiente local prod-like com smoke (G6)
- [x] Relatório de Entrega, docs raiz e CHANGELOG (G7)

## 8. Registro de Gates
<!-- Status: PENDING | PASS | FAIL | N/A. PASS e N/A exigem evidência. -->
| Gate | Status | Evidência | Data |
|---|---|---|---|
| G0 Spec | PASS | validate: 0 erro(s) — b44124e (árvore suja) | 2026-11-02 |
| G1 Red | PASS | MANUAL: red b521a31: dotnet test --project tests/Vitrine.Tooling.Tests -- --filter-trait 'spec=SPEC-0039:IT-01' → total 2, failed 1 (Ambiente_local_publica_o_Caddy_so_em_127_0_0_1: 80/tcp, 443/tcp, 443/udp sem host_ip; esperado 127.0.0.1); guarda de staging/produção verde. Teste de ferramenta (Tooling), fora do red unitário do --run. | 2026-11-02 |
| G2 Green | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (learn more: https://vitest.dev/guide/improving-performance#test-environments); lint exit 0 ([0m[34mChecked 67 files in 80[0m[0m[2m[34mms[0m[0m[34m.[0m[0m[34m No fixes app); coverage exit 0 (No lines with coverage information in this diff.) — f7cc53b | 2026-11-02 |
| G3 Arquitetura | PASS | arch_test exit 0 (duration: 3s 100ms) — f7cc53b | 2026-11-02 |
| G4 Review | PASS | verify G1+G4: PASS; revisão: Reviewer independente: APPROVED (2ª rodada; achados IPv6 e ss -ltun resolvidos pela Emenda v1.1) — 6edeb1d | 2026-11-02 |
| G5 Integração & CI | PASS | build exit 0 (npm warn install-scripts Run `npm install-scripts ls` to review, or `npm install-scripts a); test exit 0 (learn more: https://vitest.dev/guide/improving-performance#test-environments); test_integration exit 0 (duration: 4m 04s 602ms); test_e2e exit 0 (40 passed (1.4m)); arch_test exit 0 (duration: 2s 336ms); security_scan exit 0 ([90m8:12AM[0m [32mINF[0m [1mno leaks found[0m) — 7a9234e | 2026-11-02 |
| H2 Integração aprovada | PASS | política auto-on-green (aprovada por thomas em 2026-10-13); G5 PASS | 2026-11-02 |
| G6 Deploy | PASS | MANUAL: deploy local da82ad7 (main após PR #64) com CADDY_BIND_ADDRESS=127.0.0.1: deploy.sh OK (health 200 em 4 s); ss -ltun → só 127.0.0.1:80/tcp, 127.0.0.1:443/tcp, 127.0.0.1:443/udp; curl ao IP público 143.95.218.240 (Host: vitrine.localhost) http e https → 000 (sem conexão); smoke BASE_URL=https://vitrine.localhost → 4 passed (acesso + conectar-conta, desktop e mobile) | 2026-11-02 |
| G7 Pronto & Docs | PENDING | | |

## 9. Registro de Impedimentos
<!-- Toda parada é registrada pelo Architect com `spec_graph.py impede` e fechada com `resolve` — não edite à mão. Tipos: spec (spec errada/incompleta → resolve com Emenda) | decisão (só o humano decide → resposta ou ADR) | trabalho (falta algo que exige código → SPEC-NNNN nova) | externo (acesso, ambiente, terceiro → ação tomada) | falha (3 FAILs seguidos no mesmo gate → diagnóstico e decisão). Com impedimento aberto a spec aparece como parada no INDEX e não pode ser fechada. -->
| ID | Aberto em | Fase/Gate | Tipo | Descrição | Tentativas | Responsável | Resolução | Fechado em |
|---|---|---|---|---|---|---|---|---|

## 10. Relatório de Entrega
<!-- Preenchido no CLOSE (G7). Para status implemented o validate exige tudo preenchido, todo teste do plano com PASS + evidência e a Definição de Pronto marcada. -->

### O que foi entregue
O ambiente local prod-like não fica mais acessível pela internet: com `CADDY_BIND_ADDRESS=127.0.0.1` (padrão do `deploy/local/.env.example`), o Caddy publica 80/tcp, 443/tcp e 443/udp só no loopback. Staging e produção, sem a variável, continuam em todas as interfaces, IPv4 e IPv6. O runbook explica o acesso de outra máquina por túnel SSH.

### Como foi feito
- `deploy/compose.override.yaml`: portas do `caddy` como `"${CADDY_BIND_ADDRESS:+${CADDY_BIND_ADDRESS}:}80:80"` (idem 443 e 443/udp). Sem a variável a porta sai sem endereço de host, como antes (Emenda v1.1: um `0.0.0.0` explícito derrubaria o IPv6 do `AAAA` de produção).
- `deploy/local/.env.example`: `CADDY_BIND_ADDRESS=127.0.0.1`, com o motivo (o Docker passa por cima do ufw).
- `docs/runbooks/deploy.md`: variável na tabela, nota para quem já tem `deploy/local/.env`, túnel `sudo ssh -N -L 443:127.0.0.1:443` (com `-F`/`-i`/`UserKnownHostsFile` para alias) e verificação com `ss -ltun`.
- G4 em 2 rodadas: IPv6 e UDP no runbook (Emenda v1.1).

### Prova de Correção
- Red: IT-01 (`b521a31`) — `Porta 80/tcp do caddy publicada em '(sem host_ip: todas as interfaces)' no ambiente local; esperado 127.0.0.1` (idem 443/tcp e 443/udp). Red da emenda (`9f83df7`): `Porta 80/tcp do caddy publicada em '0.0.0.0' sem CADDY_BIND_ADDRESS; esperado sem host_ip (IPv4 e IPv6)`.
- Green: `8f6ffe1` + `f7cc53b`; Tooling 24/24; CI do PR #64 (runs/41434918884) verde; no ambiente local, `ss -ltun` só com `127.0.0.1` e o IP público sem conexão (antes respondia 200).

### Verificação
| Teste | Comportamento | Resultado | Evidência |
|---|---|---|---|
| IT-01 | `.env.example` local publica o Caddy só em 127.0.0.1; sem a variável, todas as interfaces (sem `host_ip`) | PASS | CI PR #64 runs/41434918884 (f7cc53b); main da82ad7 |

### Definição de Pronto
- [x] Teste de regressão falhou antes e passa depois da correção
- [x] Suíte completa, arquitetura e CI verdes (G2, G3, G5)
- [x] Review independente sem achados blocker/major (G4)
- [x] Padrão existente mantido
- [x] Disponível no ambiente-alvo via pipeline (G6)
- [x] Documentação/CHANGELOG atualizados quando aplicável (G7)
- [x] Outras ocorrências registradas como novas specs (ou nenhuma)

### Deploy
Ambiente local prod-like, `ghcr.io/thomasravache/vitrine-api:da82ad7` + `vitrine-ml-simulator:local`, 2026-11-02, com `CADDY_BIND_ADDRESS=127.0.0.1`: smoke 4/4; `ss -ltun` só `127.0.0.1:80`, `127.0.0.1:443` e `127.0.0.1:443/udp`; `curl` ao IP público → sem conexão.

### Pendências
Nenhuma. Em staging/produção nada muda (SPEC-0009 não define a variável).

## 11. Emendas
<!-- Mudança em spec aprovada: uma linha por emenda, aprovada pelo humano. -->
| Versão | Data | Mudança | Motivo | Specs impactadas | Aprovado por |
|---|---|---|---|---|---|
| v1.1 | 2026-11-02 | §3: sem `CADDY_BIND_ADDRESS` a porta sai sem endereço de host (`${CADDY_BIND_ADDRESS:+${CADDY_BIND_ADDRESS}:}`), em vez do padrão `0.0.0.0`; verificação do runbook com `ss -ltun` (inclui o 443/udp). §4: o caso staging/produção do IT-01 exige ausência de `host_ip` | G4 (Reviewer): `0.0.0.0` explícito publica só IPv4 e quebraria o `AAAA` do droplet (`infra/main.tf`, `ipv6 = true`); o objetivo aprovado ("staging e produção continuam em todas as interfaces") não muda | nenhuma | thomas (piloto automático do épico SPEC-0032) |

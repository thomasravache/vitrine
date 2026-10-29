# Runbook — Alerta `synthetic-ready`

> SPEC-0007. Regra em `deploy/alloy/grafana/alert-rules.yaml`. Responsável: thomas.

## Sintoma
A verificação sintética de `GET https://<SITE_ADDRESS>/health/ready` falhou nas 3 últimas execuções (1 por minuto): a Api não responde ou responde `503 {"status":"Unhealthy"}` (banco inacessível).

## Diagnóstico
1. `curl -sS -o /dev/null -w '%{http_code}\n' https://<SITE_ADDRESS>/health/live` — 200 = processo vivo (problema é o banco); falha = Api/Caddy fora.
2. `curl -sS https://<SITE_ADDRESS>/health/ready` — `{"status":"Unhealthy"}` confirma dependência fora.
3. No servidor: `docker compose -p vitrine ps` e `docker compose -p vitrine logs --since 15m api | grep -E '"level":"(Error|Critical)"'` (logs JSON, já mascarados).
4. Banco: status do PostgreSQL gerenciado no painel do provedor (produção) ou `docker compose -p vitrine ps postgres` (staging/local).
5. Foi logo após um deploy? `cat deploy/.state/current deploy/.state/previous`.

## Ação
- Api parada ou em loop de reinício: `docker compose -p vitrine up -d api` e acompanhar os logs.
- Banco fora: restabelecer o banco (provedor ou `docker compose -p vitrine up -d postgres`); a Api volta a `Healthy` sozinha.
- Credencial/connection string inválida após mudança de secret: corrigir o secret do Environment e reimplantar.

## Rollback
Se começou com um deploy: `deploy/rollback.sh` (ver [rollback.md](rollback.md)). Limite do Plano de Rollout: `/health/ready` falhando 3 verificações seguidas.

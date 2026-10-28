# Runbook — Alerta `http-5xx-rate`

> SPEC-0007. Regra em `deploy/alloy/grafana/alert-rules.yaml`. Responsável: thomas.

## Sintoma
Mais de 5% das respostas da Api (`http.server.request.duration`, `job="vitrine-api"`) foram 5xx nos últimos 5 minutos.

## Diagnóstico
1. Grafana → Explore (Prometheus): taxa de 5xx por `http_route` para achar a rota afetada.
2. Sentry (backend): erros novos na release atual (release = SHA da imagem) e o `trace_id` do evento.
3. Grafana → Explore (Tempo/Loki): o trace pelo `trace_id` mostra o span que falhou (HTTP, banco/Npgsql).
4. Logs do servidor: `docker compose -p vitrine logs --since 15m api | grep '"level":"Error"'`.
5. Coincide com um deploy? `cat deploy/.state/current`.

## Ação
- Dependência fora (banco): siga [synthetic-ready.md](synthetic-ready.md).
- Defeito de código numa rota: rollback e correção por spec `type: fix`.
- Saturação (memória/CPU): ver [memory-90.md](memory-90.md).

## Rollback
Limite do Plano de Rollout: 5xx > 5% em 5 min após um deploy → `deploy/rollback.sh` (ver [rollback.md](rollback.md)).

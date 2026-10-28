# Runbook — Alerta `memory-90`

> SPEC-0007. Regra em `deploy/alloy/grafana/alert-rules.yaml` (métricas do host pelo Alloy). Responsável: thomas.

## Sintoma
A memória disponível do servidor ficou abaixo de 10% por 5 minutos. Próximo passo é o OOM killer encerrar containers (Api, PostgreSQL).

## Diagnóstico
1. `free -m` e `docker stats --no-stream` no servidor — qual container consome mais.
2. Api: métricas de runtime no Grafana (`dotnet_gc_heap_size_bytes`, `process_memory_usage`) — crescimento contínuo indica vazamento.
3. `journalctl -k --since -1h | grep -i oom` — houve OOM kill?
4. Coincide com um deploy ou com um pico de tráfego (`http_server_request_duration_seconds_count`)?

## Ação
- Vazamento após deploy: rollback (abaixo) e abrir spec `type: fix`.
- Pico legítimo: reiniciar a Api libera memória (`docker compose -p vitrine restart api`); se recorrente, aumentar o droplet (SPEC de infraestrutura).

## Rollback
Se começou com um deploy: `deploy/rollback.sh` (ver [rollback.md](rollback.md)).

# Runbook — Alerta `disk-80`

> SPEC-0007. Regra em `deploy/alloy/grafana/alert-rules.yaml` (métricas do host pelo Alloy). Responsável: thomas.

## Sintoma
Um sistema de arquivos do servidor passou de 80% de uso por 5 minutos. Acima de ~95% o Docker, o PostgreSQL em container e os logs param de gravar.

## Diagnóstico
1. `df -h` no servidor; o rótulo `mountpoint` do alerta indica o volume.
2. `docker system df` — imagens antigas, containers parados e build cache.
3. `sudo du -xh --max-depth=1 /var/lib/docker | sort -h | tail` — logs de container (`containers/`) ou volumes (`volumes/`).

## Ação
- Imagens antigas (cada deploy baixa uma nova): `docker image prune -a --filter "until=168h"` — mantém as tags de `deploy/.state/current` e `previous` em uso.
- Logs de container grandes: `docker compose -p vitrine restart <serviço>` libera o arquivo; configurar rotação se recorrente.
- Volume do banco (staging/local) crescendo: avaliar retenção ou aumentar o disco do droplet.

## Rollback
N/A — não é causado por deploy. Se uma versão nova gera logs em excesso, `deploy/rollback.sh` (ver [rollback.md](rollback.md)).

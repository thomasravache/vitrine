#!/usr/bin/env bash
# SPEC-0005 — rollback em um passo: implanta a tag de deploy/.state/previous com deploy/deploy.sh,
# reaproveitando o deploy/.env atual (mesma configuração, só a tag muda).
# Uso: deploy/rollback.sh
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
previous_file="$DEPLOY_DIR/.state/previous"

[[ -f "$previous_file" ]] || { echo "rollback: ERRO: deploy/.state/previous não existe (nada para voltar)" >&2; exit 1; }
previous="$(tr -d '[:space:]' <"$previous_file")"
[[ -n "$previous" ]] || { echo "rollback: ERRO: deploy/.state/previous está vazio" >&2; exit 1; }

echo "rollback: voltando para $previous"
exec "$DEPLOY_DIR/deploy.sh" "$previous"

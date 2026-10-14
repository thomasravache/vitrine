#!/usr/bin/env bash
# SPEC-0005:IT-07 — Dado o Postgres local, quando a imagem real roda com `--migrate`, então sai
# com código 0, cria o schema do Wolverine e a aplicação não fica em execução.
#
# Local (Docker). Sobe um Postgres descartável ($PSQL_IMAGE, banco "vitrine") numa rede própria
# e roda a imagem base real (ver lib.sh: SPEC0005_BASE_IMAGE / GHCR / dotnet publish) com
# `--migrate` e ConnectionStrings__vitrine (nome do banco no AppHost). Se o processo não sair em
# IT07_TIMEOUT segundos (padrão: 120), a aplicação ficou em execução: falha sem travar.
# Schema do Wolverine: tabelas wolverine_incoming_envelopes e wolverine_outgoing_envelopes em
# qualquer schema (ou no schema exigido por IT07_WOLVERINE_SCHEMA).
set -euo pipefail
TEST_ID="IT-07"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

TIMEOUT="${IT07_TIMEOUT:-120}"
suffix="$$-$(date +%s)"
net="spec0005-it07-$suffix"
pg="spec0005-it07-pg-$suffix"
app="spec0005-it07-app-$suffix"
password="it07-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"

require_docker
require_cmd timeout
cleanup() {
  docker rm -f "$app" "$pg" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
}
trap cleanup EXIT

require_base_image
log "image: $SPEC0005_BASE_IMAGE"

# Dado: Postgres local.
docker network create "$net" >/dev/null
docker run -d --name "$pg" --network "$net" -e POSTGRES_PASSWORD="$password" -e POSTGRES_DB=vitrine \
  "$PSQL_IMAGE" >/dev/null
for ((i = 0; i < 60; i++)); do
  docker exec "$pg" pg_isready -U postgres -d vitrine >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$pg" pg_isready -U postgres -d vitrine >/dev/null 2>&1 || fail "local Postgres did not become ready"

# Quando: a imagem real roda com --migrate.
docker run -d --name "$app" --network "$net" \
  -e "ConnectionStrings__vitrine=Host=$pg;Port=5432;Database=vitrine;Username=postgres;Password=$password" \
  "$SPEC0005_BASE_IMAGE" --migrate >/dev/null
t0="$(date +%s)"
if ! code="$(timeout "$TIMEOUT" docker wait "$app")"; then
  docker logs --tail 10 "$app" 2>&1 | sed 's/^/  /' >&2
  fail "'--migrate' did not exit within ${TIMEOUT}s — the application was left running (migration mode not implemented?)"
fi
elapsed=$(($(date +%s) - t0))

# Então: sai com 0...
if [[ "$code" != "0" ]]; then
  docker logs --tail 20 "$app" 2>&1 | sed 's/^/  /' >&2
  fail "'--migrate' exited with code $code, expected 0"
fi

# ...sem ter subido a aplicação...
if docker logs "$app" 2>&1 | grep -q 'Now listening on'; then
  fail "'--migrate' started the web application (Kestrel 'Now listening on' in the logs)"
fi
[[ "$(docker inspect -f '{{.State.Running}}' "$app")" == "false" ]] || fail "application container still running"

# ...e com o schema do Wolverine criado.
schemas="$(docker exec "$pg" psql -U postgres -d vitrine -At -c \
  "SELECT DISTINCT table_schema FROM information_schema.tables
   WHERE table_name IN ('wolverine_incoming_envelopes', 'wolverine_outgoing_envelopes')
   GROUP BY table_schema HAVING count(DISTINCT table_name) = 2;")" \
  || fail "could not query the local Postgres"
[[ -n "$schemas" ]] || fail "Wolverine schema not created (no wolverine_incoming/outgoing_envelopes tables)"
if [[ -n "${IT07_WOLVERINE_SCHEMA:-}" ]]; then
  grep -qx "$IT07_WOLVERINE_SCHEMA" <<<"$schemas" \
    || fail "Wolverine tables not in schema '$IT07_WOLVERINE_SCHEMA' (found in: ${schemas//$'\n'/ })"
fi
log "Wolverine tables in schema(s): ${schemas//$'\n'/ }"

record "migrate_mode_seconds=$elapsed image=$SPEC0005_BASE_IMAGE"
pass "'--migrate' exited 0 in ${elapsed}s, created the Wolverine schema and did not start the application"

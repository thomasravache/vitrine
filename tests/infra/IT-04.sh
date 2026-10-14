#!/usr/bin/env bash
# SPEC-0005:IT-04 — Dado a versão A no ar e a imagem de prova `proof-migration-fail`, quando
# `deploy.sh proof-migration-fail` roda, então o serviço `migrate` falha, nenhum container da
# aplicação é trocado, A continua respondendo e o script sai com código 1.
#
# Local (Docker). Dublê: tests/infra/fixtures/proof-migration-fail (FROM a imagem base).
# Verifica: um container one-off do serviço `migrate` com a imagem de prova terminou com código
# != 0; os containers em execução (id, imagem, início) são idênticos antes e depois;
# /health/live respondeu 2xx em todas as sondagens durante a execução.
# Variáveis opcionais: ver lib.sh (SPEC0005_*).
set -euo pipefail
TEST_ID="IT-04"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

PROOF="proof-migration-fail"

# Containers em execução do projeto Compose da aplicação: "<id> <imagem> <início>".
snapshot() {
  docker ps -q --no-trunc --filter "label=com.docker.compose.project=$project" \
    | xargs -r docker inspect -f '{{.Id}} {{.Config.Image}} {{.State.StartedAt}}' | sort
}

require_local_env
prepare_versions
build_proof "$PROOF"

# Dado: versão A no ar.
deploy_ok "$TAG_A"
assert_live_version "$TAG_A"
project="$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' "$(api_container_id)")"
[[ -n "$project" ]] || fail "application container is not part of a Docker Compose project"
before="$(snapshot)"

# Quando: deploy.sh proof-migration-fail, sondando a disponibilidade.
probe_log="$(mktemp)"
trap 'stop_probe; rm -f "$probe_log"' EXIT
since="$(date +%s)"
start_probe "$probe_log"
log "deploy.sh $PROOF (expected to abort on migrate)"
set +e
run_deploy "$PROOF"
code=$?
set -e
stop_probe

# Então: sai com código 1...
[[ "$code" -eq 1 ]] || fail "deploy.sh $PROOF exited $code, expected 1"

# ...porque o serviço migrate falhou...
events="$(container_events_since "$since")"
migrate_fail="$(awk -v img="${LOCAL_IMAGE_REPO}:$PROOF" \
  '$1 == "die" && $2 == img && ($3 == "True" || $4 == "migrate") && $5 != "0"' <<<"$events" | wc -l)"
((migrate_fail > 0)) || fail "no 'migrate' one-off container of ${LOCAL_IMAGE_REPO}:$PROOF died with a non-zero code"
app_starts="$(awk -v img="${LOCAL_IMAGE_REPO}:$PROOF" '$1 == "start" && $2 == img && $3 != "True" && $4 != "migrate"' <<<"$events" | wc -l)"
((app_starts == 0)) || fail "an application container of ${LOCAL_IMAGE_REPO}:$PROOF was started after the migration failed"

# ...nenhum container trocado...
after="$(snapshot)"
if [[ "$after" != "$before" ]]; then
  diff <(printf '%s\n' "$before") <(printf '%s\n' "$after") >&2 || true
  fail "running containers changed during a deploy whose migration failed"
fi

# ...e A continuou respondendo.
samples="$(wc -l <"$probe_log")"
downs="$(grep -c ' down$' "$probe_log" || true)"
((samples > 0)) || fail "availability probe recorded no samples"
((downs == 0)) || fail "/health/live failed $downs of $samples probes during the aborted deploy"
assert_live_version "$TAG_A"

record "migration_fail_abort kept=$TAG_A probes=$samples downs=0"
pass "migrate failed, no container swapped, :$TAG_A kept answering, deploy.sh exited 1"

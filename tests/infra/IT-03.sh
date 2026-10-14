#!/usr/bin/env bash
# SPEC-0005:IT-03 — Dado a versão A no ar e a imagem de prova `proof-health-fail` (migração ok,
# aplicação não sobe), quando `deploy.sh proof-health-fail` roda, então o script volta para A,
# A responde 200 e o script sai com código 1.
#
# Local (Docker). Dublê: tests/infra/fixtures/proof-health-fail (FROM a imagem base).
# Prova de que a troca foi tentada (e não abortada antes): o Docker registrou o start de um
# container da aplicação (não one-off) com a imagem de prova durante a execução.
# Variáveis opcionais: ver lib.sh (SPEC0005_*).
set -euo pipefail
TEST_ID="IT-03"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

PROOF="proof-health-fail"

require_local_env
prepare_versions
build_proof "$PROOF"

# Dado: versão A no ar.
deploy_ok "$TAG_A"
assert_live_version "$TAG_A"

# Quando: deploy.sh proof-health-fail.
since="$(date +%s)"
log "deploy.sh $PROOF (expected to fail and roll back)"
set +e
run_deploy "$PROOF"
code=$?
set -e

# Então: sai com código 1...
[[ "$code" -eq 1 ]] || fail "deploy.sh $PROOF exited $code, expected 1"

# ...depois de ter tentado subir a imagem de prova como aplicação...
attempts="$(container_events_since "$since" \
  | awk -v img="${LOCAL_IMAGE_REPO}:$PROOF" '$1 == "start" && $2 == img && $3 != "True" && $4 != "migrate"' | wc -l)"
((attempts > 0)) || fail "no application container of ${LOCAL_IMAGE_REPO}:$PROOF was started — deploy.sh failed before the swap, not on the health check"
log "proof application container started $attempts time(s) before the rollback"

# ...e voltou sozinho para A, que responde 200.
assert_live_version "$TAG_A"
pass "deploy.sh $PROOF rolled back to :$TAG_A (health 200) and exited 1"

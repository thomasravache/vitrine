#!/usr/bin/env bash
# SPEC-0005:IT-02 — Dado duas versões implantadas em sequência, quando `rollback.sh` roda, então a
# versão anterior responde em `/health/live`.
#
# Local (Docker). Variáveis opcionais: ver lib.sh (SPEC0005_*).
set -euo pipefail
TEST_ID="IT-02"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_local_env
[[ -f "$ROLLBACK_SH" ]] || fail "deploy/rollback.sh does not exist"
[[ -x "$ROLLBACK_SH" ]] || fail "deploy/rollback.sh is not executable"
prepare_versions

# Dado: A e depois B.
deploy_ok "$TAG_A"
deploy_ok "$TAG_B"
assert_live_version "$TAG_B"
previous="$(state_tag previous)"
[[ "$previous" == "$TAG_A" ]] || fail "deploy/.state/previous is '$previous', expected '$TAG_A'"

# Quando: rollback em um passo.
t0="$(date +%s)"
log "rollback.sh"
run_rollback || fail "deploy/rollback.sh failed (exit $?)"
t1="$(date +%s)"

# Então: versão anterior no ar.
assert_live_version "$TAG_A"
record "rollback_seconds=$((t1 - t0)) from=$TAG_B to=$TAG_A"
pass "rollback.sh brought back :$TAG_A; /health/live 200"

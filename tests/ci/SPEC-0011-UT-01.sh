#!/usr/bin/env bash
# SPEC-0011:UT-01 — Dado o workflow de CI, quando lido pelo teste de pipeline, então o job `build-lint`
# tem o passo de verificação de drift do cliente OpenAPI depois do build.
#
# Local (lê .github/workflows/ci.yml). Passo de drift = passo de `build-lint` cujo `run` regenera o
# cliente (`api:generate`) e compara `web/src/api` com `git diff --exit-code`. Tem de vir depois do passo
# de build (comando `build` do sdd-config), não pode ter `continue-on-error` nem `if:` que o pule no PR,
# e o `run` cita a correção (`npm run api:generate`).
set -euo pipefail
TEST_ID="UT-01"
SPEC_ID="SPEC-0011"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
# shellcheck source=SPEC-0011-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0011-lib.sh"

require_ci_yml
require_cmd jq
rel="${CI_YML#"$REPO_ROOT"/}"

ci_json | jq -e --arg job "$BUILD_LINT_JOB" '.jobs[$job].steps | type == "array"' >/dev/null \
  || fail "job '$BUILD_LINT_JOB' not found (or has no steps) in $rel"

build_idx="$(build_step_index)"
[[ -n "$build_idx" ]] || fail "job '$BUILD_LINT_JOB' has no build step in $rel"

drift_idx="$(drift_step_index)"
[[ -n "$drift_idx" ]] \
  || fail "job '$BUILD_LINT_JOB' has no OpenAPI client drift step (run with 'api:generate' + 'git diff --exit-code $DRIFT_CLIENT_DIR') in $rel"

step="$(drift_step_json "$drift_idx")"
name="$(jq -r '.name // "(unnamed)"' <<<"$step")"
log "drift step #$drift_idx '$name'; build step #$build_idx"

errors=()
((drift_idx > build_idx)) || errors+=("drift step #$drift_idx comes before the build step #$build_idx")
jq -e '(.["continue-on-error"] // false) == false' <<<"$step" >/dev/null \
  || errors+=("drift step has continue-on-error")
jq -e 'has("if") | not' <<<"$step" >/dev/null \
  || errors+=("drift step has an 'if:' condition ($(jq -c '.if' <<<"$step")); it must always run")
jq -e --arg hint "$DRIFT_FIX_HINT" '.run | contains($hint)' <<<"$step" >/dev/null \
  || errors+=("drift step run does not tell how to fix ('$DRIFT_FIX_HINT')")

if ((${#errors[@]} > 0)); then
  for e in "${errors[@]}"; do printf '%s:%s   %s\n' "$SPEC_ID" "$TEST_ID" "$e" >&2; done
  fail "${#errors[@]} violation(s) in the '$BUILD_LINT_JOB' drift step of $rel"
fi
pass "job '$BUILD_LINT_JOB' runs '$name' after the build"

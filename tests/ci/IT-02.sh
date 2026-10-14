#!/usr/bin/env bash
# SPEC-0004:IT-02 — Dado um PR de prova com um teste unitário quebrado de propósito, quando o CI roda,
# então `unit` fica vermelho e o GitHub bloqueia o merge.
#
# Remoto (gh). Variáveis:
#   SPEC0004_IT02_PR   número do PR de prova (branch ci-proof/*, aberto) — obrigatória
#   SPEC0004_WAIT=1    espera os checks terminarem antes de verificar
# "Merge bloqueado" = os 7 checks são obrigatórios na proteção da main E o GitHub reporta
# mergeStateStatus BLOCKED para o PR. Nunca tenta o merge (não arrisca código quebrado na main).
set -euo pipefail
TEST_ID="IT-02"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_env SPEC0004_IT02_PR
require_number SPEC0004_IT02_PR
require_gh

pr="$SPEC0004_IT02_PR"
load_pr "$pr"
[[ "$(jq -r .state <<<"$PR_JSON")" == "OPEN" ]] || fail "PR #$pr must be OPEN to assess the merge block"

# Então (1): unit vermelho.
checks="$(pr_checks_json "$pr")"
unit="$(check_by_name "$checks" unit)"
require_check_completed "$unit"
bucket="$(jq -r .bucket <<<"$unit")"
log "check unit: $bucket ($(jq -r .link <<<"$unit"))"
[[ "$bucket" == "fail" ]] || fail "check 'unit' is '$bucket', expected 'fail'"
job_id="$(check_job_id "$unit")"
run_id="$(check_run_id "$unit")"
[[ -n "$job_id" && -n "$run_id" ]] || fail "cannot extract run/job id from the 'unit' check link"
mapfile -t failed_steps < <(job_failed_steps "$job_id")
((${#failed_steps[@]} > 0)) || fail "job 'unit' has no failed step"
log "unit failed step(s): ${failed_steps[*]}"

# Então (2): os checks obrigatórios estão na proteção da main.
mapfile -t contexts < <(required_contexts)
missing=()
for name in "${REQUIRED_CHECKS[@]}"; do
  printf '%s\n' "${contexts[@]}" | grep -qxF "$name" || missing+=("$name")
done
((${#missing[@]} == 0)) || fail "branch protection of $BASE_BRANCH does not require: ${missing[*]} (has: ${contexts[*]:-none})"

# Então (3): GitHub bloqueia o merge.
merge_state="$(gh pr view "$pr" -R "$GH_REPO" --json mergeStateStatus --jq .mergeStateStatus)"
log "mergeStateStatus: $merge_state"
[[ "$merge_state" == "BLOCKED" ]] || fail "mergeStateStatus is '$merge_state', expected 'BLOCKED'"
enforce_admins="$(gh api "repos/$GH_REPO/branches/$BASE_BRANCH/protection/enforce_admins" --jq .enabled 2>/dev/null || echo "?")"

record "pr=$PR_URL unit=$(jq -r .link <<<"$unit") mergeStateStatus=$merge_state enforce_admins=$enforce_admins"
pass "PR #$pr: 'unit' red and merge BLOCKED by required checks"

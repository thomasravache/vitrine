#!/usr/bin/env bash
# SPEC-0004:IT-01 — Dado um PR de prova com mudança trivial, quando o CI roda, então os 7 checks
# obrigatórios terminam verdes (link da execução como evidência).
#
# Remoto (gh). Variáveis:
#   SPEC0004_IT01_PR   número do PR de prova (branch ci-proof/*, base main) — obrigatória
#   SPEC0004_WAIT=1    espera os checks terminarem antes de verificar
# Registra o link e a duração (início do 1º check → fim do último) para a métrica de ≤ 15 min (G5).
set -euo pipefail
TEST_ID="IT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_env SPEC0004_IT01_PR
require_number SPEC0004_IT01_PR
require_gh

# Dado: PR de prova.
pr="$SPEC0004_IT01_PR"
load_pr "$pr"

# Então: os 7 checks obrigatórios, do workflow CI, verdes.
checks="$(pr_checks_json "$pr")"
not_green=()
for name in "${REQUIRED_CHECKS[@]}"; do
  c="$(check_by_name "$checks" "$name")"
  require_check_completed "$c"
  bucket="$(jq -r .bucket <<<"$c")"
  log "check $name: $bucket ($(jq -r .link <<<"$c"))"
  [[ "$bucket" == "pass" ]] || not_green+=("$name=$bucket")
done
((${#not_green[@]} == 0)) || fail "required checks not green: ${not_green[*]}"

run_id="$(check_run_id "$(check_by_name "$checks" "${REQUIRED_CHECKS[0]}")")"
[[ -n "$run_id" ]] || fail "cannot extract the run id from the check link"
load_run "$run_id"
[[ "$(jq -r .head_sha <<<"$RUN_JSON")" == "$PR_HEAD_SHA" ]] \
  || fail "run $run_id is for $(jq -r .head_sha <<<"$RUN_JSON"), not the PR head $PR_HEAD_SHA (stale run?)"
duration="$(jq -r --argjson n "$(printf '%s\n' "${REQUIRED_CHECKS[@]}" | jq -R . | jq -s .)" \
  '[.[] | select(.name as $x | $n | index($x))] |
   ((map(.completedAt | fromdateiso8601) | max) - (map(.startedAt | fromdateiso8601) | min))' <<<"$checks" 2>/dev/null || echo "?")"
record "pr=$PR_URL run=$(jq -r .html_url <<<"$RUN_JSON") head_sha=$PR_HEAD_SHA duration_s=$duration all_7_green"
pass "PR #$pr: ${REQUIRED_CHECKS[*]} all green"

#!/usr/bin/env bash
# SPEC-0004:CT-01 — Dado um PR de prova que cita uma spec sem gates registrados, quando o job `sdd`
# roda `pr-check`, então falha com a anotação correspondente.
#
# Remoto (gh). Variáveis:
#   SPEC0004_CT01_PR        número do PR de prova (branch ci-proof/*) — obrigatória
#   SPEC0004_WAIT=1         espera os checks terminarem antes de verificar
#   SPEC0004_CT01_SPEC      spec citada sem gates (padrão: a 1ª SPEC-NNNN do título do PR)
#   SPEC0004_CT01_ANNOT_RE  regex da anotação (padrão: mensagem do pr-check para gates ausentes,
#                           "[gates] <SPEC>: sem PASS/N/A com evidência em G…")
# Anotação = annotation de nível failure no check run `sdd` (pr-check imprime ::error title=SDD::…).
set -euo pipefail
TEST_ID="CT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_env SPEC0004_CT01_PR
require_number SPEC0004_CT01_PR
require_gh

# Dado: PR de prova citando uma spec sem gates.
pr="$SPEC0004_CT01_PR"
load_pr "$pr"
spec="${SPEC0004_CT01_SPEC:-$(jq -r .title <<<"$PR_JSON" | grep -oE 'SPEC-[0-9]{4}' | head -n1 || true)}"
[[ -n "$spec" ]] || fail "PR #$pr title cites no SPEC-NNNN; set SPEC0004_CT01_SPEC"
annot_re="${SPEC0004_CT01_ANNOT_RE:-\[gates\] $spec: sem PASS/N/A com evid.ncia em G}"
log "spec cited: $spec"

# Quando/Então: job sdd vermelho com a anotação do pr-check.
checks="$(pr_checks_json "$pr")"
sdd="$(check_by_name "$checks" sdd)"
require_check_completed "$sdd"
bucket="$(jq -r .bucket <<<"$sdd")"
log "check sdd: $bucket ($(jq -r .link <<<"$sdd"))"
[[ "$bucket" == "fail" ]] || fail "check 'sdd' is '$bucket', expected 'fail'"
job_id="$(check_job_id "$sdd")"
[[ -n "$job_id" ]] || fail "cannot extract the job id from the 'sdd' check link"

annotations="$(check_annotations "$job_id")" || fail "cannot read annotations of check run $job_id"
match="$(awk -F'\t' '$1 == "failure"' <<<"$annotations" | grep -E "$annot_re" | head -n1 || true)"
if [[ -z "$match" ]]; then
  printf '%s\n' "$annotations" | sed "s/^/$SPEC_ID:$TEST_ID   annotation: /" >&2
  fail "no failure annotation on 'sdd' matches /$annot_re/"
fi
log "annotation: $match"

record "pr=$PR_URL sdd=$(jq -r .link <<<"$sdd") spec=$spec annotation=$(cut -f3 <<<"$match" | tr ' ' '_')"
pass "PR #$pr: 'sdd' (pr-check) red with the missing-gates annotation for $spec"

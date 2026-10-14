#!/usr/bin/env bash
# SPEC-0004:IT-03 — Dado um PR de prova com um token falso em arquivo, quando o CI roda, então
# `security` falha com achado do gitleaks.
#
# Remoto (gh). Variáveis:
#   SPEC0004_IT03_PR        número do PR de prova (branch ci-proof/*) — obrigatória
#   SPEC0004_WAIT=1         espera os checks terminarem antes de verificar
#   SPEC0004_IT03_STEP_RE   regex do nome do passo do gitleaks (padrão: gitleaks|secret|security[ _-]?scan —
#                           aceita um passo único que rode o `security_scan` do sdd-config)
#   SPEC0004_IT03_LOG_RE    regex do achado no log (padrão: saída do gitleaks "leaks found: N", N ≥ 1)
# Token falso: formato reconhecido pelo gitleaks, nunca um token real (seção 7, dublês).
set -euo pipefail
TEST_ID="IT-03"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_env SPEC0004_IT03_PR
require_number SPEC0004_IT03_PR
require_gh

step_re="${SPEC0004_IT03_STEP_RE:-gitleaks|secret|security[ _-]?scan}"
log_re="${SPEC0004_IT03_LOG_RE:-leaks found: *[1-9]}"

pr="$SPEC0004_IT03_PR"
load_pr "$pr"
assert_security_failed_by "$pr" "$step_re" "$log_re"

record "pr=$PR_URL security=$(jq -r .link <<<"$SECURITY_CHECK") failed_steps=${SECURITY_FAILED_STEPS[*]// /_}"
pass "PR #$pr: 'security' red with a gitleaks finding"

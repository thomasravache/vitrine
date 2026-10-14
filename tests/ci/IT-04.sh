#!/usr/bin/env bash
# SPEC-0004:IT-04 — Dado um PR de prova que adiciona uma versão de pacote com vulnerabilidade alta
# conhecida, quando o CI roda, então `security` falha.
#
# Remoto (gh). Variáveis:
#   SPEC0004_IT04_PR        número do PR de prova (branch ci-proof/*) — obrigatória
#   SPEC0004_WAIT=1         espera os checks terminarem antes de verificar
#   SPEC0004_IT04_STEP_RE   regex do passo de varredura de dependências (padrão: vulnerab|dotnet list package|audit|trivy|security[ _-]?scan)
#   SPEC0004_IT04_LOG_RE    regex do achado no log (padrão: saídas de `dotnet list package --vulnerable`,
#                           `npm audit` e Trivy com severidade alta/crítica)
# O PR precisa alterar um manifesto de dependências; a falha não pode ser do gitleaks.
set -euo pipefail
TEST_ID="IT-04"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_env SPEC0004_IT04_PR
require_number SPEC0004_IT04_PR
require_gh

step_re="${SPEC0004_IT04_STEP_RE:-vulnerab|dotnet list package|audit|trivy|security[ _-]?scan}"
log_re="${SPEC0004_IT04_LOG_RE:-has the following vulnerable packages|[0-9]+ (high|critical) severity vulnerabilit|severity: (high|critical)|(HIGH|CRITICAL): [1-9]}"
manifest_re='(^|/)(Directory\.Packages\.props|[^/]+\.csproj|packages\.lock\.json|package\.json|package-lock\.json)$'

# Dado: PR que mexe num manifesto de dependências.
pr="$SPEC0004_IT04_PR"
load_pr "$pr"
mapfile -t manifests < <(jq -r '.files[]?.path' <<<"$PR_JSON" | grep -E "$manifest_re" || true)
((${#manifests[@]} > 0)) || fail "PR #$pr does not change any dependency manifest (Directory.Packages.props, *.csproj, package.json, lock files)"
log "changed manifests: ${manifests[*]}"

# Então: security vermelho pela varredura de dependências (não pelo gitleaks).
assert_security_failed_by "$pr" "$step_re" "$log_re"
! grep -qiE 'leaks found: *[1-9]' <<<"$SECURITY_LOG" \
  || fail "gitleaks also reported leaks — the proof PR must contain no secret-like content"

record "pr=$PR_URL security=$(jq -r .link <<<"$SECURITY_CHECK") manifests=${manifests[*]} failed_steps=${SECURITY_FAILED_STEPS[*]// /_}"
pass "PR #$pr: 'security' red by a vulnerable dependency"

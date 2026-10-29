#!/usr/bin/env bash
# SPEC-0011:IT-01 — Dado uma cópia do repositório com um endpoint alterado e o cliente não regenerado,
# quando o passo de verificação roda, então sai com código diferente de zero e aponta `npm run api:generate`.
#
# Local e lento (build da API + orval, duas vezes). Exige git, dotnet, node/npm e jq.
# O passo executado é o `run` do passo de drift do job `build-lint` de .github/workflows/ci.yml (o mesmo
# que o CI roda), numa cópia do repositório (arquivos versionados + não versionados não ignorados, com
# um commit próprio), com o `env:` do workflow/job/passo. Controle: na cópia intacta o passo sai com 0 (sem falso positivo); depois a rota
# de um endpoint muda sem regenerar o cliente e o passo tem de falhar citando a correção.
# Variáveis (opcionais):
#   SPEC0011_IT01_KEEP=1   mantém a cópia temporária (caminho no log) para inspeção
#   SPEC0011_IT01_ENV      "K=V" separados por quebra de linha, aplicados por cima do `env:` do ci.yml
#                          (ex.: apontar ConnectionStrings__vitrine para um PostgreSQL local, se o passo usar um)
set -euo pipefail
TEST_ID="IT-01"
SPEC_ID="SPEC-0011"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
# shellcheck source=SPEC-0011-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0011-lib.sh"

ENDPOINT_FILE="src/Modules/Identity/Vitrine.Identity.Presentation/IdentityEndpoints.cs"
ENDPOINT_FROM='"/api/v1/me"'
ENDPOINT_TO='"/api/v1/me-drift-probe"'

require_ci_yml
require_cmd jq git tar dotnet node npm
rel="${CI_YML#"$REPO_ROOT"/}"

drift_idx="$(drift_step_index)"
[[ -n "$drift_idx" ]] \
  || fail "job '$BUILD_LINT_JOB' has no OpenAPI client drift step (run with 'api:generate' + 'git diff --exit-code $DRIFT_CLIENT_DIR') in $rel"
step="$(drift_step_json "$drift_idx")"
run_script="$(jq -r '.run' <<<"$step")"
workdir="$(jq -r '.["working-directory"] // "."' <<<"$step")"
[[ "$run_script" != *'${{'* ]] || fail "drift step run uses \${{ }} expressions; cannot run it outside Actions"
# `env:` do workflow, do job e do passo (nessa precedência, como no Actions); valores com expressões não entram.
mapfile -t step_env < <(ci_json | jq -r --arg job "$BUILD_LINT_JOB" --argjson i "$drift_idx" '
  (.env // {}) + (.jobs[$job].env // {}) + (.jobs[$job].steps[$i].env // {})
  | to_entries[] | select((.value | tostring) | contains("${{") | not) | "\(.key)=\(.value)"')
if [[ -n "${SPEC0011_IT01_ENV:-}" ]]; then
  while IFS= read -r kv; do
    [[ -z "$kv" ]] || step_env+=("$kv")
  done <<<"$SPEC0011_IT01_ENV"
fi
log "drift step '$(jq -r '.name // "(unnamed)"' <<<"$step")' (working-directory: $workdir)"

# Dado: cópia do repositório com commit próprio.
work="$(mktemp -d)"
if [[ "${SPEC0011_IT01_KEEP:-}" == "1" ]]; then
  log "keeping copy at $work"
else
  trap 'rm -rf "$work"' EXIT
fi
copy="$work/repo"
mkdir -p "$copy"
(cd "$REPO_ROOT" && git ls-files -z --cached --others --exclude-standard) \
  | (cd "$REPO_ROOT" && tar --null --ignore-failed-read -T - -cf -) \
  | (cd "$copy" && tar -xf -)
git -C "$copy" init -q
git -C "$copy" add -A
git -C "$copy" -c user.name=spec-0011 -c user.email=spec-0011@invalid -c commit.gpgsign=false \
  commit -qm "SPEC-0011:IT-01 baseline" --no-verify

# Dependências da SPA (no CI vêm do `npm ci` do passo de build).
if [[ -d "$REPO_ROOT/web/node_modules" ]]; then
  ln -s "$REPO_ROOT/web/node_modules" "$copy/web/node_modules"
else
  log "npm ci (web) in the copy"
  npm --prefix "$copy/web" ci --no-audit --no-fund >"$work/npm-ci.log" 2>&1 \
    || { tail -n 40 "$work/npm-ci.log" >&2; fail "npm ci failed in the copy"; }
fi

run_step() {
  local out="$1"
  (cd "$copy/$workdir" && env "${step_env[@]}" bash -eo pipefail -c "$run_script") >"$out" 2>&1
}

# Controle: cliente em dia -> passo verde.
log "control: running the drift step on the untouched copy"
if ! run_step "$work/control.log"; then
  tail -n 60 "$work/control.log" | sed "s/^/$SPEC_ID:$TEST_ID   control: /" >&2
  fail "drift step fails on an untouched copy (false positive, or the committed client is already stale)"
fi
git -C "$copy" diff --quiet || fail "control run left changes in the copy: $(git -C "$copy" diff --stat | tail -n1)"

# Quando: endpoint alterado, cliente não regenerado.
grep -qF "$ENDPOINT_FROM" "$copy/$ENDPOINT_FILE" || fail "fixture endpoint $ENDPOINT_FROM not found in $ENDPOINT_FILE"
sed -i "s#$ENDPOINT_FROM#$ENDPOINT_TO#" "$copy/$ENDPOINT_FILE"
git -C "$copy" -c user.name=spec-0011 -c user.email=spec-0011@invalid -c commit.gpgsign=false \
  commit -qam "SPEC-0011:IT-01 endpoint changed without api:generate" --no-verify
log "changed $ENDPOINT_FROM -> $ENDPOINT_TO in $ENDPOINT_FILE; running the drift step"

set +e
run_step "$work/drift.log"
code=$?
set -e

# Então: sai != 0 e aponta a correção.
if ((code == 0)); then
  tail -n 40 "$work/drift.log" | sed "s/^/$SPEC_ID:$TEST_ID   drift: /" >&2
  fail "drift step exited 0 with a stale client"
fi
if ! grep -qF "$DRIFT_FIX_HINT" "$work/drift.log"; then
  tail -n 40 "$work/drift.log" | sed "s/^/$SPEC_ID:$TEST_ID   drift: /" >&2
  fail "drift step exited $code but its output does not point to '$DRIFT_FIX_HINT'"
fi
log "drift step exited $code: $(grep -F "$DRIFT_FIX_HINT" "$work/drift.log" | head -n1)"
pass "stale OpenAPI client detected (exit $code) with the '$DRIFT_FIX_HINT' hint"

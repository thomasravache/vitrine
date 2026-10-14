#!/usr/bin/env bash
# SPEC-0004:UT-01 — Dado `ci.yml`, quando validado com `actionlint`, então não há erros e todos os
# `uses:` de terceiros estão fixados por SHA.
#
# Local. Usa actionlint instalado ou a imagem oficial $ACTIONLINT_IMAGE.
# "Terceiros" = toda referência remota (inclusive actions/* e github/*): só `./…` (local) dispensa.
#   owner/repo[/path]@<sha de 40 hex>     ação ou workflow reutilizável
#   docker://<imagem>@sha256:<64 hex>     imagem de container
set -euo pipefail
TEST_ID="UT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_cmd jq
rel="${CI_YML#"$REPO_ROOT"/}"

# Quando: actionlint.
log "actionlint $rel"
if command -v actionlint >/dev/null 2>&1; then
  (cd "$REPO_ROOT" && actionlint -no-color "$CI_YML") || fail "actionlint reported errors in $rel"
elif [[ "$CI_YML" == "$REPO_ROOT"/* ]]; then
  require_docker
  docker run --rm -u "$(id -u):$(id -g)" -v "$REPO_ROOT:/repo:ro" -w /repo "$ACTIONLINT_IMAGE" -no-color "$rel" \
    || fail "actionlint reported errors in $rel"
else
  require_docker
  docker run --rm -u "$(id -u):$(id -g)" -v "$(dirname "$CI_YML"):/wf:ro" -w /wf "$ACTIONLINT_IMAGE" -no-color \
    "$(basename "$CI_YML")" || fail "actionlint reported errors in $rel"
fi

# Então: todo `uses:` remoto fixado por SHA completo.
json="$(yaml_to_json "$CI_YML")" || fail "cannot parse $rel as YAML"
mapfile -t uses < <(jq -r '.jobs // {} | to_entries[] | .key as $j
  | ((.value.uses // empty | "\($j)\t\(.)"), (.value.steps // [] | .[] | .uses // empty | "\($j)\t\(.)"))' <<<"$json")
((${#uses[@]} > 0)) || fail "$rel has no 'uses:' at all (expected at least actions/checkout)"

bad=()
for entry in "${uses[@]}"; do
  job="${entry%%$'\t'*}"
  ref="${entry#*$'\t'}"
  if [[ "$ref" == ./* ]]; then
    continue
  elif [[ "$ref" == docker://* ]]; then
    [[ "$ref" =~ @sha256:[0-9a-f]{64}$ ]] || bad+=("$job: $ref (docker image not pinned by @sha256 digest)")
  elif [[ ! "$ref" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$ ]]; then
    bad+=("$job: $ref (not pinned by full commit SHA)")
  fi
done
if ((${#bad[@]} > 0)); then
  for e in "${bad[@]}"; do printf '%s:%s   %s\n' "$SPEC_ID" "$TEST_ID" "$e" >&2; done
  fail "${#bad[@]} 'uses:' not pinned by SHA in $rel"
fi

pass "actionlint clean; ${#uses[@]} 'uses:' all local or pinned by SHA"

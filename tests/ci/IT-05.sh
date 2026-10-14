#!/usr/bin/env bash
# SPEC-0004:IT-05 — Dado um merge na `main`, quando o CI conclui, então a imagem
# `ghcr.io/thomasravache/vitrine-api:<sha>` existe e o artefato `compose-bundle` está anexado à execução.
#
# Remoto (gh + registro GHCR). Variáveis (uma das duas é obrigatória):
#   SPEC0004_IT05_RUN_ID   ID da execução do CI de push na main após o merge
#   SPEC0004_IT05_SHA      SHA do merge commit na main (resolve a execução de CI verde desse SHA)
# Contrato: execução "CI", evento push, branch main, sucesso, com os 7 jobs verdes; artefato
# compose-bundle com docker-compose.yaml e .env.template; imagem :<sha>; se for a execução verde mais
# recente da main, a tag "main" aponta para o mesmo digest.
# Pacote GHCR privado: `docker login ghcr.io` ou token do gh com read:packages.
set -euo pipefail
TEST_ID="IT-05"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
if [[ -z "${SPEC0004_IT05_RUN_ID:-}" && -z "${SPEC0004_IT05_SHA:-}" ]]; then
  fail "env var(s) missing: SPEC0004_IT05_RUN_ID or SPEC0004_IT05_SHA (see the header of tests/ci/$TEST_ID.sh)"
fi
require_gh

# Dado: execução do CI do merge na main.
if [[ -n "${SPEC0004_IT05_RUN_ID:-}" ]]; then
  require_number SPEC0004_IT05_RUN_ID
  load_run "$SPEC0004_IT05_RUN_ID"
else
  [[ "$SPEC0004_IT05_SHA" =~ ^[0-9a-f]{40}$ ]] || fail "SPEC0004_IT05_SHA must be a full 40-hex SHA"
  RUN_JSON="$(successful_main_run_for_sha "$SPEC0004_IT05_SHA" 2>&1)" \
    || fail "cannot query '$CI_WORKFLOW_FILE' runs in $GH_REPO (workflow not on GitHub yet?): $RUN_JSON"
  [[ -n "$RUN_JSON" ]] || fail "no successful '$CI_WORKFLOW_NAME' push run on $BASE_BRANCH for $SPEC0004_IT05_SHA"
fi
run_id="$(jq -r .id <<<"$RUN_JSON")"
sha="$(jq -r .head_sha <<<"$RUN_JSON")"
log "run $(jq -r .html_url <<<"$RUN_JSON") head_sha=$sha"
[[ "$(jq -r .name <<<"$RUN_JSON")" == "$CI_WORKFLOW_NAME" ]] || fail "run $run_id is workflow '$(jq -r .name <<<"$RUN_JSON")', expected '$CI_WORKFLOW_NAME'"
[[ "$(jq -r .event <<<"$RUN_JSON")" == "push" ]] || fail "run $run_id event is '$(jq -r .event <<<"$RUN_JSON")', expected 'push'"
[[ "$(jq -r .head_branch <<<"$RUN_JSON")" == "$BASE_BRANCH" ]] || fail "run $run_id is on '$(jq -r .head_branch <<<"$RUN_JSON")', expected '$BASE_BRANCH'"
[[ "$(jq -r .conclusion <<<"$RUN_JSON")" == "success" ]] || fail "run $run_id conclusion is '$(jq -r .conclusion <<<"$RUN_JSON")', expected 'success'"

jobs="$(gh api --paginate "repos/$GH_REPO/actions/runs/$run_id/jobs" --jq '.jobs[] | [.name, (.conclusion // "")] | @tsv')" \
  || fail "cannot list jobs of run $run_id"
for name in "${REQUIRED_CHECKS[@]}"; do
  conclusion="$(awk -F'\t' -v n="$name" '$1 == n { c = $2 } END { print c }' <<<"$jobs")"
  [[ "$conclusion" == "success" ]] || fail "job '$name' of run $run_id is '${conclusion:-absent}', expected 'success'"
done

# Então (1): compose-bundle anexado.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
assert_compose_bundle "$run_id" "$work/bundle"

# Então (2): imagem :<sha> no GHCR.
digest="$(image_digest "$sha")"
[[ -n "$digest" ]] || fail "image $API_IMAGE_REPO:$sha not found in GHCR (private package? 'docker login ghcr.io' or gh token with read:packages)"
log "image $API_IMAGE_REPO:$sha -> $digest"

latest="$(gh api "repos/$GH_REPO/actions/workflows/$CI_WORKFLOW_FILE/runs?event=push&branch=$BASE_BRANCH&status=success&per_page=1" --jq '.workflow_runs[0].id // empty' 2>/dev/null || true)"
main_note="main-tag=not-checked(newer run on $BASE_BRANCH)"
if [[ "$latest" == "$run_id" ]]; then
  main_digest="$(image_digest main)"
  [[ "$main_digest" == "$digest" ]] || fail "tag :main is '${main_digest:-absent}', expected the digest of :$sha ($digest)"
  main_note="main-tag=$digest"
fi

record "run=$(jq -r .html_url <<<"$RUN_JSON") image=$API_IMAGE_REPO:$sha digest=$digest artifact=$COMPOSE_BUNDLE_ARTIFACT $main_note"
pass "main run $run_id: $API_IMAGE_REPO:$sha published and '$COMPOSE_BUNDLE_ARTIFACT' attached"

#!/usr/bin/env bash
# SPEC-0004:CT-02 — Dado uma execução de CI concluída na `main`, quando um workflow de prova consome
# `workflow_run` e busca a imagem e o artefato pelo `head_sha`, então ambos são encontrados
# (contrato com a SPEC-0005).
#
# Remoto (gh + registro GHCR). Variáveis:
#   SPEC0004_CT02_RUN_ID           ID da execução do CI (push na main) — obrigatória; é o objeto que o
#                                  evento workflow_run entrega ao consumidor (github.event.workflow_run)
#   SPEC0004_CT02_CONSUMER_RUN_ID  opcional: execução do workflow de prova disparada por workflow_run;
#                                  verifica que ela foi disparada por esse CI, para esse head_sha, e passou
# O script repete o que o consumidor faz: a partir só do payload (name, conclusion, event, head_branch,
# head_sha), localiza pelo head_sha a execução de CI verde, o artefato compose-bundle e a imagem :<sha>.
set -euo pipefail
TEST_ID="CT-02"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_ci_yml
require_env SPEC0004_CT02_RUN_ID
require_number SPEC0004_CT02_RUN_ID
require_gh

# Dado: payload workflow_run de uma execução de CI concluída na main.
load_run "$SPEC0004_CT02_RUN_ID"
payload="$(jq -c '{id, name, conclusion, event, head_branch, head_sha, html_url}' <<<"$RUN_JSON")"
log "workflow_run payload: $payload"
[[ "$(jq -r .name <<<"$payload")" == "$CI_WORKFLOW_NAME" ]] || fail "payload.name is not '$CI_WORKFLOW_NAME'"
[[ "$(jq -r .conclusion <<<"$payload")" == "success" ]] || fail "payload.conclusion is '$(jq -r .conclusion <<<"$payload")', expected 'success'"
[[ "$(jq -r .event <<<"$payload")" == "push" ]] || fail "payload.event is '$(jq -r .event <<<"$payload")', expected 'push'"
[[ "$(jq -r .head_branch <<<"$payload")" == "$BASE_BRANCH" ]] || fail "payload.head_branch is not '$BASE_BRANCH'"
sha="$(jq -r .head_sha <<<"$payload")"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || fail "payload.head_sha '$sha' is not a full SHA"

# Quando: o consumidor busca pelo head_sha (não pelo id da execução).
resolved="$(successful_main_run_for_sha "$sha" 2>&1)" \
  || fail "cannot query '$CI_WORKFLOW_FILE' runs in $GH_REPO by head_sha: $resolved"
[[ -n "$resolved" ]] || fail "no successful '$CI_WORKFLOW_NAME' push run on $BASE_BRANCH found by head_sha $sha"
resolved_id="$(jq -r .id <<<"$resolved")"
log "resolved by head_sha $sha -> run $resolved_id"

# Então: artefato e imagem encontrados.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
assert_compose_bundle "$resolved_id" "$work/bundle"
digest="$(image_digest "$sha")"
[[ -n "$digest" ]] || fail "image $API_IMAGE_REPO:$sha not found by head_sha (private package? 'docker login ghcr.io' or gh token with read:packages)"
log "image $API_IMAGE_REPO:$sha -> $digest"

consumer_note="consumer=not-provided"
if [[ -n "${SPEC0004_CT02_CONSUMER_RUN_ID:-}" ]]; then
  require_number SPEC0004_CT02_CONSUMER_RUN_ID
  consumer="$(gh api "repos/$GH_REPO/actions/runs/$SPEC0004_CT02_CONSUMER_RUN_ID")" \
    || fail "cannot read consumer run $SPEC0004_CT02_CONSUMER_RUN_ID"
  [[ "$(jq -r .event <<<"$consumer")" == "workflow_run" ]] || fail "consumer run event is '$(jq -r .event <<<"$consumer")', expected 'workflow_run'"
  [[ "$(jq -r .head_sha <<<"$consumer")" == "$sha" ]] || fail "consumer run head_sha is not $sha"
  [[ "$(jq -r .conclusion <<<"$consumer")" == "success" ]] || fail "consumer run conclusion is '$(jq -r .conclusion <<<"$consumer")', expected 'success'"
  consumer_note="consumer=$(jq -r .html_url <<<"$consumer")"
fi

record "ci_run=$(jq -r .html_url <<<"$payload") head_sha=$sha resolved_run=$resolved_id image=$API_IMAGE_REPO:$sha digest=$digest artifact=$COMPOSE_BUNDLE_ARTIFACT $consumer_note"
pass "head_sha $sha resolves to $API_IMAGE_REPO:$sha and '$COMPOSE_BUNDLE_ARTIFACT' (run $resolved_id)"

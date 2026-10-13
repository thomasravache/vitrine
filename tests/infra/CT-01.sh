#!/usr/bin/env bash
# SPEC-0005:CT-01 — Dado uma execução de CI concluída na `main` (contrato SPEC-0004@1), quando o
# `deploy.yml` resolve a tag pelo `head_sha`, então encontra a imagem no GHCR e o artefato
# `compose-bundle`.
#
# Parte 1 (local): .github/workflows/deploy.yml consome o contrato — gatilho workflow_run do
#   workflow "CI" (completed, main), workflow_dispatch com inputs environment e tag, e tag
#   resolvida pelo head_sha.
# Parte 2 (remota): para o head_sha de uma execução de CI verde na main existem
#   ghcr.io/thomasravache/vitrine-api:<head_sha> e o artefato compose-bundle (não expirado).
#
# Variáveis:
#   GH_REPO          owner/repo (padrão: deduzido do remote origin)
#   CT01_HEAD_SHA    SHA a resolver (padrão: último CI verde de push na main)
#   Pacote GHCR privado: `docker login ghcr.io` antes (token com read:packages).
set -euo pipefail
TEST_ID="CT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

DEPLOY_YML="${DEPLOY_YML:-$REPO_ROOT/.github/workflows/deploy.yml}"

# --- Parte 1: o consumidor declara o contrato --------------------------------
require_file "$DEPLOY_YML" ".github/workflows/deploy.yml does not exist"
require_cmd python3 jq
problems="$(python3 -I - "$DEPLOY_YML" "$CI_WORKFLOW" "$API_IMAGE_REPO" "$COMPOSE_BUNDLE_ARTIFACT" <<'PY'
import sys
import yaml

path, ci_name, image, artifact = sys.argv[1:5]
text = open(path, encoding="utf-8").read()
doc = yaml.safe_load(text) or {}
on = doc.get("on", doc.get(True)) or {}
problems = []

def as_list(v):
    return v if isinstance(v, list) else ([] if v is None else [v])

wr = on.get("workflow_run") if isinstance(on, dict) else None
if not isinstance(wr, dict):
    problems.append("missing on.workflow_run")
else:
    if ci_name not in as_list(wr.get("workflows")):
        problems.append(f"on.workflow_run.workflows does not include '{ci_name}'")
    if "completed" not in as_list(wr.get("types")):
        problems.append("on.workflow_run.types does not include 'completed'")
    if "main" not in as_list(wr.get("branches")):
        problems.append("on.workflow_run.branches does not include 'main'")

wd = on.get("workflow_dispatch") if isinstance(on, dict) else None
inputs = (wd.get("inputs") or {}) if isinstance(wd, dict) else {}
if not isinstance(wd, dict):
    problems.append("missing on.workflow_dispatch")
for name in ("environment", "tag"):
    if name not in inputs:
        problems.append(f"workflow_dispatch input '{name}' missing")
env_in = inputs.get("environment") or {}
if isinstance(env_in, dict) and env_in.get("type") == "choice":
    opts = set(as_list(env_in.get("options")))
    if opts != {"staging", "production"}:
        problems.append(f"environment options {sorted(opts)} != ['production', 'staging']")

if "head_sha" not in text:
    problems.append("deploy.yml does not resolve the tag from the workflow_run head_sha")

print("\n".join(problems))
PY
)" || fail "could not parse $DEPLOY_YML"
[[ -z "$problems" ]] || fail "deploy.yml does not consume the SPEC-0004@1 contract: ${problems//$'\n'/; }"
log "deploy.yml declares the SPEC-0004@1 consumer contract"

# --- Parte 2: o provedor entrega imagem e bundle pelo SHA ---------------------
if [[ -z "${GH_REPO:-}" ]]; then
  origin="$(git -C "$REPO_ROOT" remote get-url origin 2>/dev/null || true)"
  GH_REPO="$(sed -nE 's#^.*github\.com[:/]([^/]+/[^/.]+)(\.git)?$#\1#p' <<<"$origin")"
fi
require_env GH_REPO
require_gh

filter=(-R "$GH_REPO" -w "$CI_WORKFLOW" -b main -e push -s success -L 1 --json "databaseId,headSha,url")
[[ -z "${CT01_HEAD_SHA:-}" ]] || filter+=(-c "$CT01_HEAD_SHA")
runs="$(gh run list "${filter[@]}" 2>&1)" \
  || fail "cannot list '$CI_WORKFLOW' runs in $GH_REPO (provider SPEC-0004 not delivered yet?): $runs"
ci="$(jq '.[0] // empty' <<<"$runs")"
[[ -n "$ci" ]] || fail "no successful '$CI_WORKFLOW' push run on main in $GH_REPO${CT01_HEAD_SHA:+ for $CT01_HEAD_SHA}"
sha="$(jq -r .headSha <<<"$ci")"
ci_id="$(jq -r .databaseId <<<"$ci")"
log "CI run: $(jq -r .url <<<"$ci") head_sha=$sha"

artifacts="$(gh api "repos/$GH_REPO/actions/runs/$ci_id/artifacts")" || fail "cannot list artifacts of CI run $ci_id"
bundle="$(jq --arg n "$COMPOSE_BUNDLE_ARTIFACT" '[.artifacts[] | select(.name == $n and .expired == false)] | length' <<<"$artifacts")"
((bundle > 0)) || fail "artifact '$COMPOSE_BUNDLE_ARTIFACT' not found (or expired) in CI run $ci_id"
log "artifact $COMPOSE_BUNDLE_ARTIFACT found in run $ci_id"

require_docker
docker manifest inspect "${API_IMAGE_REPO}:${sha}" >/dev/null 2>&1 \
  || fail "image ${API_IMAGE_REPO}:${sha} not found in GHCR (private package? run 'docker login ghcr.io')"
log "image ${API_IMAGE_REPO}:${sha} found"

pass "head_sha $sha resolves to ${API_IMAGE_REPO}:$sha and artifact $COMPOSE_BUNDLE_ARTIFACT"

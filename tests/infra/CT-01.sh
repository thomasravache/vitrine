#!/usr/bin/env bash
# SPEC-0005:CT-01 — Dado uma execução de CI concluída na `main` (contrato SPEC-0004@1), quando
# `deploy/resolve-release.sh <head_sha>` roda, então imprime a imagem do GHCR e a execução com o
# artefato `compose-bundle`.
#
# Contrato (seção 6): resolve-release.sh <sha> imprime ghcr.io/thomasravache/vitrine-api:<sha> e o ID
# da execução de CI com o artefato compose-bundle; sai != 0 se algum não existir.
# Verifica também, de forma independente (gh + docker), que imagem e artefato existem, e que um
# SHA sem release faz o script sair != 0.
#
# Variáveis:
#   GH_REPO          owner/repo (padrão: deduzido do remote origin)
#   CT01_HEAD_SHA    SHA a resolver (padrão: último CI verde de push na main)
#   Pacote GHCR privado: `docker login ghcr.io` antes (token com read:packages).
set -euo pipefail
TEST_ID="CT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

RESOLVE_SH="$REPO_ROOT/deploy/resolve-release.sh"
[[ -f "$RESOLVE_SH" ]] || fail "deploy/resolve-release.sh does not exist"
[[ -x "$RESOLVE_SH" ]] || fail "deploy/resolve-release.sh is not executable"

if [[ -z "${GH_REPO:-}" ]]; then
  origin="$(git -C "$REPO_ROOT" remote get-url origin 2>/dev/null || true)"
  GH_REPO="$(sed -nE 's#^.*github\.com[:/]([^/]+/[^/.]+)(\.git)?$#\1#p' <<<"$origin")"
fi
require_env GH_REPO
export GH_REPO
require_gh
require_docker

# Dado: execução de CI verde na main.
filter=(-R "$GH_REPO" -w "$CI_WORKFLOW" -b main -e push -s success -L 1 --json "databaseId,headSha,url")
[[ -z "${CT01_HEAD_SHA:-}" ]] || filter+=(-c "$CT01_HEAD_SHA")
runs="$(gh run list "${filter[@]}" 2>&1)" \
  || fail "cannot list '$CI_WORKFLOW' runs in $GH_REPO (provider SPEC-0004 not delivered yet?): $runs"
ci="$(jq '.[0] // empty' <<<"$runs")"
[[ -n "$ci" ]] || fail "no successful '$CI_WORKFLOW' push run on main in $GH_REPO${CT01_HEAD_SHA:+ for $CT01_HEAD_SHA}"
sha="$(jq -r .headSha <<<"$ci")"
ci_id="$(jq -r .databaseId <<<"$ci")"
log "CI run: $(jq -r .url <<<"$ci") head_sha=$sha"

# Provedor entrega imagem e bundle pelo SHA (verificação independente).
artifacts="$(gh api "repos/$GH_REPO/actions/runs/$ci_id/artifacts")" || fail "cannot list artifacts of CI run $ci_id"
bundle="$(jq --arg n "$COMPOSE_BUNDLE_ARTIFACT" '[.artifacts[] | select(.name == $n and .expired == false)] | length' <<<"$artifacts")"
((bundle > 0)) || fail "artifact '$COMPOSE_BUNDLE_ARTIFACT' not found (or expired) in CI run $ci_id"
docker manifest inspect "${API_IMAGE_REPO}:${sha}" >/dev/null 2>&1 \
  || fail "image ${API_IMAGE_REPO}:${sha} not found in GHCR (private package? run 'docker login ghcr.io')"

# Quando: resolve-release.sh <head_sha>.
set +e
out="$(cd "$REPO_ROOT" && "$RESOLVE_SH" "$sha" 2>&1)"
code=$?
set -e
log "resolve-release.sh $sha -> exit $code"
printf '%s\n' "$out" | sed "s/^/  /"

# Então: imprime a imagem do GHCR e a execução com o compose-bundle.
[[ "$code" -eq 0 ]] || fail "resolve-release.sh $sha exited $code, expected 0"
grep -qF "${API_IMAGE_REPO}:${sha}" <<<"$out" || fail "output does not contain the image ${API_IMAGE_REPO}:${sha}"
grep -qw "$ci_id" <<<"$out" || fail "output does not contain the CI run id $ci_id (run with the $COMPOSE_BUNDLE_ARTIFACT artifact)"

# SHA sem release: sai != 0.
unknown_sha="0000000000000000000000000000000000000000"
set +e
(cd "$REPO_ROOT" && "$RESOLVE_SH" "$unknown_sha" >/dev/null 2>&1)
code=$?
set -e
((code != 0)) || fail "resolve-release.sh $unknown_sha (no release) exited 0, expected != 0"

pass "resolve-release.sh $sha -> ${API_IMAGE_REPO}:$sha and CI run $ci_id ($COMPOSE_BUNDLE_ARTIFACT); unknown sha exits $code"

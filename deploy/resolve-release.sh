#!/usr/bin/env bash
# SPEC-0005 — localiza o release de um commit (contrato SPEC-0004@1).
# Uso: deploy/resolve-release.sh <sha>
# Imprime (stdout, uma por linha):
#   image=ghcr.io/thomasravache/vitrine-api:<sha>
#   run_id=<ID da execução do CI "CI" na main com o artefato compose-bundle>
# Sai != 0 se a imagem ou a execução com o artefato não existir.
# Requer: gh autenticado (GH_TOKEN), jq, docker. GH_REPO = owner/repo (padrão: o do diretório atual).
# Variáveis opcionais: IMAGE_REPO, CI_WORKFLOW (padrão: CI), COMPOSE_BUNDLE_ARTIFACT (padrão: compose-bundle).
set -euo pipefail

IMAGE_REPO="${IMAGE_REPO:-ghcr.io/thomasravache/vitrine-api}"
CI_WORKFLOW="${CI_WORKFLOW:-CI}"
COMPOSE_BUNDLE_ARTIFACT="${COMPOSE_BUNDLE_ARTIFACT:-compose-bundle}"

die() { printf 'resolve-release: ERRO: %s\n' "$*" >&2; exit 1; }

(($# == 1)) || { echo "uso: deploy/resolve-release.sh <sha>" >&2; exit 2; }
sha="$1"
[[ "$sha" =~ ^[0-9a-f]{7,40}$ ]] || die "SHA inválido: $sha"
for c in gh jq docker; do command -v "$c" >/dev/null 2>&1 || die "comando não encontrado: $c"; done

repo_args=()
[[ -z "${GH_REPO:-}" ]] || repo_args=(-R "$GH_REPO")

# Execução do CI concluída com sucesso para o SHA, na main, com o artefato compose-bundle não expirado.
runs="$(gh run list "${repo_args[@]}" -w "$CI_WORKFLOW" -b main -s success -c "$sha" -L 20 \
  --json databaseId,headSha 2>/dev/null)" || die "não foi possível listar as execuções de '$CI_WORKFLOW'"
run_id=""
for id in $(jq -r --arg sha "$sha" '.[] | select(.headSha | startswith($sha)) | .databaseId' <<<"$runs"); do
  repo="${GH_REPO:-$(gh repo view --json nameWithOwner -q .nameWithOwner)}"
  count="$(gh api "repos/$repo/actions/runs/$id/artifacts" \
    --jq "[.artifacts[] | select(.name == \"$COMPOSE_BUNDLE_ARTIFACT\" and .expired == false)] | length" 2>/dev/null || echo 0)"
  if ((count > 0)); then
    run_id="$id"
    full_sha="$(jq -r --argjson id "$id" '.[] | select(.databaseId == $id) | .headSha' <<<"$runs")"
    break
  fi
done
[[ -n "$run_id" ]] || die "nenhuma execução de '$CI_WORKFLOW' com sucesso na main e artefato '$COMPOSE_BUNDLE_ARTIFACT' para $sha"

image="${IMAGE_REPO}:${full_sha}"
docker manifest inspect "$image" >/dev/null 2>&1 || die "imagem $image não encontrada no registry"

printf 'image=%s\n' "$image"
printf 'run_id=%s\n' "$run_id"

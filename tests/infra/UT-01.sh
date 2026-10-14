#!/usr/bin/env bash
# SPEC-0005:UT-01 — Dado infra/, quando `tofu validate` e `tflint` rodam, então não há erros.
#
# Local (sem nuvem): roda sobre uma cópia de infra/ (não suja o repositório), com
# `tofu init -backend=false` (baixa providers, não toca o estado remoto) + `tofu validate`
# e `tflint --init` + `tflint --recursive`. Usa tofu/tflint locais se instalados;
# senão as imagens oficiais $TOFU_IMAGE e $TFLINT_IMAGE.
set -euo pipefail
TEST_ID="UT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

INFRA_DIR="${INFRA_DIR:-$REPO_ROOT/infra}"

log "infra dir: ${INFRA_DIR#"$REPO_ROOT"/}"
[[ -d "$INFRA_DIR" ]] || fail "infra/ does not exist (${INFRA_DIR#"$REPO_ROOT"/}) — nothing to validate"
compgen -G "$INFRA_DIR/*.tf" >/dev/null || fail "infra/ has no *.tf files"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp -a "$INFRA_DIR/." "$work/"
rm -rf "$work/.terraform"

run_tofu() {
  if command -v tofu >/dev/null 2>&1; then
    (cd "$work" && tofu "$@")
  else
    require_docker
    docker run --rm -u "$(id -u):$(id -g)" -e HOME=/work/.home -e TF_IN_AUTOMATION=1 \
      -v "$work:/work" -w /work "$TOFU_IMAGE" "$@"
  fi
}

run_tflint() {
  if command -v tflint >/dev/null 2>&1; then
    (cd "$work" && TFLINT_PLUGIN_DIR="$work/.tflint.d" tflint "$@")
  else
    require_docker
    docker run --rm -u "$(id -u):$(id -g)" -e HOME=/work/.home -e TFLINT_PLUGIN_DIR=/work/.tflint.d \
      -v "$work:/work" -w /work --entrypoint tflint "$TFLINT_IMAGE" "$@"
  fi
}

mkdir -p "$work/.home" "$work/.tflint.d"

log "tofu init -backend=false"
run_tofu init -backend=false -input=false -no-color >/dev/null || fail "tofu init -backend=false failed"

log "tofu validate"
run_tofu validate -no-color || fail "tofu validate reported errors"

log "tflint --init"
run_tflint --init || fail "tflint --init failed"

log "tflint --recursive"
run_tflint --recursive --format compact --no-color || fail "tflint reported issues"

pass "tofu validate and tflint report no errors"

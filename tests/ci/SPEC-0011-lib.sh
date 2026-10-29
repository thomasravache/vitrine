# shellcheck shell=bash
# SPEC-0011 — utilitários comuns de UT-01/IT-01 (checagem de drift do cliente OpenAPI no CI).
# Carregado depois de lib.sh. Localiza no job `build-lint` de ci.yml o passo que regenera o cliente
# (`api:generate`) e compara `web/src/api` com o versionado (`git diff --exit-code` ou `git status --porcelain`).

BUILD_LINT_JOB="build-lint"
DRIFT_CLIENT_DIR="web/src/api"
DRIFT_FIX_HINT="npm run api:generate"

# JSON do workflow, carregado uma vez.
ci_json() {
  if [[ -z "${CI_JSON:-}" ]]; then
    CI_JSON="$(yaml_to_json "$CI_YML")" || fail "cannot parse ${CI_YML#"$REPO_ROOT"/} as YAML"
  fi
  printf '%s' "$CI_JSON"
}

# Índice (0-based) do passo de drift em build-lint, ou vazio.
drift_step_index() {
  ci_json | jq -r --arg job "$BUILD_LINT_JOB" --arg dir "$DRIFT_CLIENT_DIR" '
    [(.jobs[$job].steps // []) | to_entries[]
     | select((.value.run // "") | (contains("api:generate") and contains($dir)
         and ((contains("git diff") and contains("--exit-code")) or (contains("git status") and contains("--porcelain")))))
     | .key] | first // empty'
}

# Índice do passo de build (comando `build` do sdd-config) em build-lint, ou vazio.
build_step_index() {
  ci_json | jq -r --arg job "$BUILD_LINT_JOB" '
    [(.jobs[$job].steps // []) | to_entries[]
     | select(((.value.run // "") | test("yq -r .\\.build.")) or ((.value.name // "") | test("^Build\\b")))
     | .key] | first // empty'
}

drift_step_json() {
  local idx="$1"
  ci_json | jq -c --arg job "$BUILD_LINT_JOB" --argjson i "$idx" '.jobs[$job].steps[$i]'
}

DRIFT_STEP_MISSING="has no OpenAPI client drift step (run with 'api:generate' + 'git diff --exit-code' or 'git status --porcelain' on $DRIFT_CLIENT_DIR)"

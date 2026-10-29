# shellcheck shell=bash
# SPEC-0012 — utilitários comuns de UT-01/IT-01 (o runner tests/infra/run.sh derruba só a stack que subiu).
# Carregado depois de lib.sh (que fixa SPEC_ID=SPEC-0005: o script redefine SPEC_ID depois do source).
#
# "Stack local" = projeto Compose `vitrine` (name: em deploy/compose.yaml), o mesmo de deploy/deploy.sh.
# O IT de fixture SPEC-0012-FX-up.sh sobe só o postgres desse projeto, pelos mesmos arquivos do Compose
# que o deploy.sh usa (deploy/compose.yaml + deploy/compose.override.yaml), sem portas 80/443 e sem
# precisar de imagem da API: é o "IT que sobe a stack" mínimo, rápido e determinístico.

COMPOSE_PROJECT="vitrine"
RUN_SH="$INFRA_TESTS_DIR/run.sh"
# shellcheck disable=SC2034 # usado pelos scripts que carregam este arquivo
FIXTURE_IT="SPEC-0012-FX-up"
COMPOSE_BASE_FILE="$REPO_ROOT/deploy/compose.yaml"
COMPOSE_OVERRIDE_FILE="$REPO_ROOT/deploy/compose.override.yaml"

# IDs dos containers em execução do projeto (o critério da spec: `docker compose -p vitrine ps -q`).
stack_ids() {
  docker compose -p "$COMPOSE_PROJECT" ps -q 2>/dev/null | sort
}

stack_is_up() {
  [[ -n "$(stack_ids)" ]]
}

# Arquivo de ambiente sintético para o Compose (deploy/local/.env.example + IMAGE_TAG exigido na interpolação).
fixture_env_file() {
  local f
  f="$(mktemp)"
  cp "$ENV_EXAMPLE" "$f"
  printf '\nIMAGE_TAG=spec0012-fixture\n' >>"$f"
  printf '%s\n' "$f"
}

# Sobe o postgres do projeto `vitrine` (não recria containers que já estejam no ar).
fixture_up() {
  local env_file rc=0
  env_file="$(fixture_env_file)"
  docker compose --project-directory "$REPO_ROOT/deploy" --env-file "$env_file" \
    -f "$COMPOSE_BASE_FILE" -f "$COMPOSE_OVERRIDE_FILE" \
    up -d --no-recreate --wait --quiet-pull postgres >&2 || rc=$?
  rm -f "$env_file"
  return "$rc"
}

# Derruba o projeto `vitrine` (containers, redes e volumes), como o runner deve fazer.
stack_down() {
  docker compose -p "$COMPOSE_PROJECT" down -v --remove-orphans >/dev/null 2>&1 || true
}

# Roda o runner (tests/infra/run.sh <args>) e devolve o código de saída; saída em $RUN_OUTPUT.
run_runner() {
  local rc=0
  RUN_OUTPUT="$(bash "$RUN_SH" "$@" 2>&1)" || rc=$?
  return "$rc"
}

runner_tail() {
  tail -n 15 <<<"$RUN_OUTPUT" | sed "s/^/$SPEC_ID:$TEST_ID   run.sh: /" >&2
}

# Volumes do projeto (rótulo do Compose), um por linha, ordenados.
stack_volumes() {
  docker volume ls -q --filter "label=com.docker.compose.project=$COMPOSE_PROJECT" 2>/dev/null | sort
}

# Precondição de UT-01: nenhuma stack `vitrine` no ar. Com SPEC0012_TAKE_DOWN=1 o teste a derruba
# (docker compose -p vitrine down, sem -v); sem isso, falha sem tocar no ambiente de quem roda.
require_no_stack() {
  stack_is_up || return 0
  if [[ "${SPEC0012_TAKE_DOWN:-}" == "1" ]]; then
    log "precondition: taking down the running '$COMPOSE_PROJECT' stack (SPEC0012_TAKE_DOWN=1)"
    docker compose -p "$COMPOSE_PROJECT" down >/dev/null 2>&1 || fail "could not take down the '$COMPOSE_PROJECT' stack"
    stack_is_up && fail "the '$COMPOSE_PROJECT' stack is still up after 'docker compose down'"
    return 0
  fi
  fail "precondition: the Compose project '$COMPOSE_PROJECT' is already up; take it down ('docker compose -p $COMPOSE_PROJECT down') or set SPEC0012_TAKE_DOWN=1"
}

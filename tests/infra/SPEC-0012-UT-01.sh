#!/usr/bin/env bash
# SPEC-0012:UT-01 — Dado nenhuma stack `vitrine` no ar, quando `run.sh` executa um IT que sobe a stack e
# termina, então `docker compose -p vitrine ps -q` fica vazio; com `--keep`, os containers continuam.
#
# Local (Docker). O IT executado é a fixture SPEC-0012-FX-up (sobe só o postgres do projeto `vitrine`
# pelos arquivos do Compose de deploy/ e não o derruba). Dois casos:
#   1. `tests/infra/run.sh SPEC-0012-FX-up`         -> sai 0 e nenhum container do projeto fica no ar;
#   2. `tests/infra/run.sh --keep SPEC-0012-FX-up`  -> sai 0 e os containers continuam no ar.
# Precondição: nenhuma stack `vitrine` no ar (SPEC0012_TAKE_DOWN=1 deixa o teste derrubá-la).
# Entre os casos e ao sair, o teste derruba o que sobrou do projeto (`docker compose -p vitrine down -v`,
# como o runner): o volume postgres_data do ambiente local é apagado (só dados sintéticos).
set -euo pipefail
TEST_ID="UT-01"
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
SPEC_ID="SPEC-0012"
# shellcheck source=SPEC-0012-lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/SPEC-0012-lib.sh"

require_docker
require_file "$RUN_SH" "tests/infra/run.sh does not exist"
require_no_stack
trap stack_down EXIT

errors=()

# Caso 1 — sem --keep: o runner derruba a stack que subiu.
log "run.sh $FIXTURE_IT (no stack before)"
if run_runner "$FIXTURE_IT"; then
  left="$(stack_ids)"
  if [[ -n "$left" ]]; then
    errors+=("run.sh $FIXTURE_IT left $(wc -l <<<"$left") container(s) of '$COMPOSE_PROJECT' running (docker compose -p $COMPOSE_PROJECT ps -q not empty)")
  else
    log "after run.sh: docker compose -p $COMPOSE_PROJECT ps -q is empty"
  fi
else
  runner_tail
  errors+=("run.sh $FIXTURE_IT exited non-zero")
fi
stack_down

# Caso 2 — com --keep: o runner preserva a stack.
log "run.sh --keep $FIXTURE_IT (no stack before)"
if run_runner --keep "$FIXTURE_IT"; then
  if stack_is_up; then
    log "after run.sh --keep: $(stack_ids | wc -l) container(s) still running"
  else
    errors+=("run.sh --keep $FIXTURE_IT took the '$COMPOSE_PROJECT' stack down (expected it to be kept)")
  fi
else
  runner_tail
  errors+=("run.sh --keep $FIXTURE_IT exited non-zero (is --keep supported?)")
fi

if ((${#errors[@]} > 0)); then
  for e in "${errors[@]}"; do printf '%s:%s   %s\n' "$SPEC_ID" "$TEST_ID" "$e" >&2; done
  fail "${#errors[@]} violation(s): run.sh must take down the stack it brought up, unless --keep"
fi
pass "run.sh takes down the '$COMPOSE_PROJECT' stack it brought up; --keep keeps it"
